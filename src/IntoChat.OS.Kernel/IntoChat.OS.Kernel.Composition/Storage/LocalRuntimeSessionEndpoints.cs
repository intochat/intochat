using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Spi;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Storage;

internal sealed class LocalRuntimeSessionHub
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Channel<SseItem<string>>> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string BrainId, long Sequence), CapabilityCaller?> _provenance = new();
    private long _watermark;

    public LocalRuntimeSessionHub(IServiceProvider services)
    {
        _services = services;
    }

    public async Task SubscribeClusterAsync(IClusterListeners listeners)
    {
        await listeners.Listen<Rejected>("kernel.session.rejected", OnSignalAsync).ConfigureAwait(false);
        var signalTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static a => !a.IsDynamic)
            .SelectMany(SafeGetTypes)
            .Where(static t => t is { IsClass: true, IsAbstract: false }
                && typeof(DigitalBrain.Signal).IsAssignableFrom(t)
                && t != typeof(Rejected)
                && t.IsDefined(typeof(DigitalBrain.SignalAttribute), inherit: false))
            .Distinct()
            .ToArray();

        var listenMethod = typeof(IClusterListeners).GetMethod(nameof(IClusterListeners.Listen))!;
        var handlerMethod = typeof(LocalRuntimeSessionHub).GetMethod(nameof(OnSignalGenericAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var type in signalTypes)
        {
            try
            {
                var genericListen = listenMethod.MakeGenericMethod(type);
                var delegateType = typeof(Func<,,,>).MakeGenericType(typeof(DigitalBrain.NeuronId), type, typeof(CancellationToken), typeof(Task));
                var handler = Delegate.CreateDelegate(delegateType, this, handlerMethod.MakeGenericMethod(type));
                var task = (Task)genericListen.Invoke(listeners, [$"kernel.session.{type.FullName}", handler])!;
                await task.ConfigureAwait(false);
            }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException)
            {
                continue;
            }
            catch (ArgumentException error)
            {
                _ = error.Message;
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }

    private Task OnSignalGenericAsync<TSignal>(DigitalBrain.NeuronId publisher, TSignal signal, CancellationToken ct)
        where TSignal : DigitalBrain.Signal
        => OnSignalAsync(publisher, signal, ct);

    private async Task OnSignalAsync(DigitalBrain.NeuronId publisher, DigitalBrain.Signal signal, CancellationToken ct)
    {
        var caller = RuntimeCallerContext.TryGet(out var stamped) ? stamped : null;
        var brainId = publisher.Brain.Value;
        var fullKey = $"{brainId}/{publisher.Key}";
        var alias = signal.GetType().GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias ?? signal.GetType().Name;

        if (_services.GetService<ISignalJournal>() is { } journal)
        {
            var grainId = Orleans.Runtime.GrainId.Create(publisher.Type, fullKey);
            var seq = await journal.Append(grainId, [signal]).ConfigureAwait(false);
            _provenance[(brainId, seq)] = caller;
        }

        var matching = _subscriptions.Values
            .Where(sub => string.Equals(sub.BrainId, brainId, StringComparison.Ordinal)
                && (string.Equals(sub.Key, fullKey, StringComparison.Ordinal) || string.Equals(sub.Key, publisher.Key, StringComparison.Ordinal))
                && (string.Equals(sub.Signal, alias, StringComparison.Ordinal) || string.Equals(sub.Signal, signal.GetType().Name, StringComparison.Ordinal)))
            .ToArray();

        if (matching.Length == 0) { return; }

        var payloadJson = SerializeWithProvenance(signal, caller);
        foreach (var sub in matching)
        {
            var mark = Interlocked.Increment(ref _watermark);
            var batch = new SignalBatchEvent(sub.Id, [payloadJson], mark);
            var item = new SseItem<string>(JsonSerializer.Serialize(batch, RuntimeProtocol.Json), SignalBatchEvent.Kind);
            foreach (var (_, channel) in _streams.Where(pair => pair.Key.StartsWith(brainId + ":", StringComparison.Ordinal)))
            {
                channel.Writer.TryWrite(item);
            }
        }
    }

    private static string SerializeWithProvenance(DigitalBrain.Signal signal, CapabilityCaller? caller)
    {
        var node = JsonSerializer.SerializeToNode(signal, signal.GetType(), RuntimeProtocol.Json) as JsonObject ?? new JsonObject();
        if (caller is not null)
        {
            node["provenance"] = JsonSerializer.SerializeToNode(caller, RuntimeProtocol.Json);
        }
        return node.ToJsonString(RuntimeProtocol.Json);
    }

    public string RegisterSubscription(string brainId, SubscribeReport subscribe)
    {
        var id = "sub-" + Guid.NewGuid().ToString("N");
        _subscriptions[id] = new SubscriptionEntry(id, brainId, subscribe.Behavior, subscribe.Contract, subscribe.Key, subscribe.Signal);
        return id;
    }

    public async IAsyncEnumerable<SseItem<string>> StreamAsync(string brainId, [EnumeratorCancellation] CancellationToken ct)
    {
        var streamKey = $"{brainId}:{Guid.NewGuid():N}";
        var channel = Channel.CreateUnbounded<SseItem<string>>();
        _streams[streamKey] = channel;
        try
        {
            var ready = new ReadyEvent("local", []);
            yield return new SseItem<string>(JsonSerializer.Serialize(ready, RuntimeProtocol.Json), ReadyEvent.Kind);
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            _streams.TryRemove(streamKey, out _);
            channel.Writer.TryComplete();
        }
    }

    private sealed record SubscriptionEntry(string Id, string BrainId, string Behavior, string Contract, string Key, string Signal);
}

internal sealed class LocalRuntimeSessionStartup(LocalRuntimeSessionHub hub, IClusterListeners listeners) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe<LocalRuntimeSessionStartup>(
            ServiceLifecycleStage.Active,
            _ => hub.SubscribeClusterAsync(listeners));
    }
}

internal static class LocalRuntimeSessionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var edge = BrainRoutes.SessionGroup(endpoints).AllowAnonymous().ExcludeFromDescription();
        edge.MapPost(RuntimeProtocol.Report, (string brainId, RuntimeReport report, LocalRuntimeSessionHub hub) =>
        {
            if (report.Subscribe is { } subscribe)
            {
                var id = hub.RegisterSubscription(brainId, subscribe);
                return Results.Ok(new { subscription = id });
            }
            return Results.Ok();
        });
        edge.MapGet(RuntimeProtocol.Stream, (string brainId, LocalRuntimeSessionHub hub, CancellationToken cancellationToken) =>
            TypedResults.ServerSentEvents(hub.StreamAsync(brainId, cancellationToken)));
        edge.MapPost(RuntimeProtocol.Invoke, async (HttpContext http, string brainId, NeuronSurface surface, CancellationToken cancellationToken) =>
        {
            var invocation = await http.Request.ReadFromJsonAsync<RuntimeInvocation>(RuntimeProtocol.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("An invocation is required.");
            var argsJson = JsonSerializer.Serialize(invocation.Arguments, RuntimeProtocol.Json);
            var value = await surface.Call(invocation.Contract, invocation.Key, invocation.Method, argsJson, cancellationToken).ConfigureAwait(false);
            return value is { } element ? Results.Json(element, RuntimeProtocol.Json) : Results.NoContent();
        });
    }
}
