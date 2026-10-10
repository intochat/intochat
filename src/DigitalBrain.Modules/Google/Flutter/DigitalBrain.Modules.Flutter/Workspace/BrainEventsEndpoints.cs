using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Flutter.Workspace;

// GET /brains/{brainId}/brain/events: the whole brain as `brain-snapshot` server-sent events. The first
// snapshot lists every neuron the kernel knows; another follows each time one is created or retired.
internal static class BrainEventsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints)
        => BrainRoutes.Group(endpoints).MapGet("/brain/events", Events);

    private static async Task Events(HttpContext http, IDigitalBrain brain, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.Body.FlushAsync(ct);
        await foreach (var snapshot in Snapshots(brain, BrainScope.CurrentId(), ct))
        {
            await http.Response.WriteAsync("event: brain-snapshot\ndata: " + snapshot + "\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    private static readonly TimeSpan Burst = TimeSpan.FromMilliseconds(100);

    // The first snapshot is written at once. Watches on the kernel's instance announcements and on the
    // brain runtime follow; once every watch is registered the brain is read again, so nothing that
    // happened in between is missed, and a snapshot identical to the last one is not sent. A burst of
    // changes becomes one snapshot. A faulted kernel watch ends the stream with its error, so the
    // client sees a failure, never a clean end. The runtime is optional: a brain without one has no synapses.
    internal static async IAsyncEnumerable<string> Snapshots(IDigitalBrain brain, string brainId, [EnumeratorCancellation] CancellationToken ct)
    {
        var changes = Channel.CreateUnbounded<bool>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runtime = brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(brainId));
        var waiting = 3;
        void Subscribed() { if (Interlocked.Decrement(ref waiting) == 0) { changes.Writer.TryWrite(true); } }
        _ = Watch(brain.On<InstanceCreated>(brain.Self, () => { Subscribed(); return Task.CompletedTask; }, stop.Token), required: true);
        _ = Watch(brain.On<InstanceRetired>(brain.Self, () => { Subscribed(); return Task.CompletedTask; }, stop.Token), required: true);
        _ = Watch(brain.On<BrainRuntimeChanged>(runtime, () => { Subscribed(); return Task.CompletedTask; }, stop.Token), required: false);
        try
        {
            var (json, key) = await Snapshot(brain, runtime, brainId, ct);
            var last = key;
            yield return json;
            await foreach (var _tick in changes.Reader.ReadAllAsync(ct))
            {
                await Task.Delay(Burst, ct);
                while (changes.Reader.TryRead(out var more)) { _ = more; }
                (json, key) = await Snapshot(brain, runtime, brainId, ct);
                if (key == last) { continue; }
                last = key;
                yield return json;
            }
        }
        finally { await stop.CancelAsync(); }

        async Task Watch<T>(IAsyncEnumerable<T> signals, bool required)
        {
            try { await foreach (var _ in signals) { changes.Writer.TryWrite(true); } }
            catch (OperationCanceledException) { }
            catch (Exception error) when (!required)
            {
                _ = error;
                Subscribed();
            }
            catch (Exception error) { changes.Writer.TryComplete(error); }
        }
    }

    private static async Task<(string Json, string Key)> Snapshot(IDigitalBrain brain, IBrainRuntime runtime, string brainId, CancellationToken ct)
    {
        BrainRuntimeSnapshot? running = null;
        try { running = await runtime.Inspect(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        var behaviors = running?.Behaviors ?? [];
        var connected = running?.Session == SessionStatus.Connected;

        List<string> keys = [];
        List<(string Key, string Type)> instances = [];
        await foreach (var instance in brain.Kernel.Instances(null, ct)) { instances.Add((instance.Key, instance.Type)); }
        keys.AddRange(instances.Select(instance => instance.Key));

        string? AppOf(string behavior) => keys
            .Where(key => IApp.NameOf(brainId, key) is not null && (behavior == key || behavior.StartsWith(key + "/", StringComparison.Ordinal)))
            .OrderByDescending(key => key.Length).FirstOrDefault();

        List<object> nodes = [];
        foreach (var (key, type) in instances)
        {
            var name = key[(key.LastIndexOf('/') + 1)..];
            var active = connected && behaviors.Any(behavior => AppOf(behavior.Id) == key);
            nodes.Add(new { id = key, type, name, label = name, module = "", role = "observed", status = active ? "Running" : "Idle", handledSignals = Array.Empty<string>() });
        }

        List<object> synapses = [];
        foreach (var behavior in behaviors)
        {
            if (AppOf(behavior.Id) is not { } app) { continue; }
            foreach (var subscription in behavior.Subscriptions)
            {
                var source = GrainId.TryParse(subscription.Neuron, out var grain) ? grain.Key.ToString()! : subscription.Neuron;
                synapses.Add(new
                {
                    id = $"{behavior.Id}|{source}|{subscription.Signal}",
                    sourceId = source,
                    targetId = app,
                    signalType = subscription.Signal,
                    kind = "subscription",
                    fireCount = subscription.Delivered,
                });
            }
        }
        var identity = JsonSerializer.Serialize(new { nodes, synapses }, Json);
        var json = JsonSerializer.Serialize(new
        {
            rootId = brainId,
            observedAt = DateTimeOffset.UtcNow,
            scope = "brain",
            truncated = false,
            nodes,
            synapses,
            activity = Array.Empty<object>(),
            correlations = Array.Empty<object>(),
        }, Json);
        return (json, identity);
    }
}
