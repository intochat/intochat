using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Orleans;

namespace DigitalBrain.Flutter.Workspace;

// One server-sent stream of the signals a shell screen renders live: GET /brains/{id}/live?type=A&type=B.
// Each event is {"type","publisher","payload"}. A source's last retained signal of the type is sent first,
// so a screen opened late starts from the current state instead of waiting for the next change.
internal static class LiveSignalEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    // Sources yields the neurons that publish the type: those that exist now, then any that appear later.
    internal sealed record LiveFeed(
        string Type,
        Func<IDigitalBrain, string, CancellationToken, IAsyncEnumerable<INeuron>> Sources,
        Func<IDigitalBrain, INeuron, Func<Task>, CancellationToken, IAsyncEnumerable<Signal>> Watch);

    internal static LiveFeed Feed<T>(
        Func<IDigitalBrain, string, CancellationToken, IAsyncEnumerable<INeuron>> sources) where T : Signal
        => new(typeof(T).Name, sources, (brain, source, subscribed, ct) => brain.On<T>(source, subscribed, ct));

    internal static readonly LiveFeed[] Feeds =
    [
        Feed<ClusterChanged>(static (brain, brainId, _) => Single(brain.Get<ICluster>(ICluster.Key(brainId)))),
        Feed<AppActivity>(Apps),
    ];

    private static async IAsyncEnumerable<INeuron> Single(INeuron neuron) { await Task.CompletedTask; yield return neuron; }

    // Apps already there, then each one the kernel announces: the watch is registered before the listing, so none is missed.
    private static async IAsyncEnumerable<INeuron> Apps(IDigitalBrain brain, string brainId, [EnumeratorCancellation] CancellationToken ct)
    {
        var keys = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var created in brain.On<InstanceCreated>(brain.Self, async () =>
                {
                    await foreach (var instance in brain.Kernel.Instances<IApp>(ct)) { keys.Writer.TryWrite(instance.Key); }
                }, ct)) { keys.Writer.TryWrite(created.Key); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { keys.Writer.TryComplete(error); }
        }, CancellationToken.None);
        HashSet<string> seen = [];
        await foreach (var key in keys.Reader.ReadAllAsync(ct))
        {
            if (IApp.NameOf(brainId, key) is not null && seen.Add(key)) { yield return brain.Get<IApp>(key); }
        }
    }

    public static void Map(IEndpointRouteBuilder endpoints)
        => BrainRoutes.Group(endpoints).MapGet("/live", Live);

    private static async Task Live(HttpContext http, IDigitalBrain brain, ILoggerFactory logs, CancellationToken ct)
    {
        var (selected, error) = Select(http.Request.Query["type"].Where(name => !string.IsNullOrEmpty(name)).Select(name => name!));
        if (error is not null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error }, ct);
            return;
        }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.Body.FlushAsync(ct);
        await foreach (var envelope in Stream(brain, BrainScope.CurrentId(), selected, logs.CreateLogger("DigitalBrain.Flutter.Live"), ct))
        {
            await http.Response.WriteAsync("data: " + envelope + "\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    internal static (LiveFeed[] Feeds, string? Error) Select(IEnumerable<string> types)
    {
        var requested = types.Distinct().ToArray();
        var unknown = requested.Where(name => Feeds.All(feed => feed.Type != name)).ToArray();
        if (requested.Length == 0) { return ([], "Name at least one signal type."); }
        if (unknown.Length > 0) { return ([], $"Not a live signal: {string.Join(", ", unknown)}."); }
        return ([.. Feeds.Where(feed => requested.Contains(feed.Type))], null);
    }

    // The envelopes as JSON text, retained signals first per source, then live ones, until ct ends the stream.
    // The stream stays open with no sources at all: a source may appear later. A source or feed that fails is logged and dropped.
    internal static async IAsyncEnumerable<string> Stream(IDigitalBrain brain, string brainId, LiveFeed[] feeds, ILogger log, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            foreach (var feed in feeds) { _ = Task.Run(() => Discover(brain, brainId, feed, channel.Writer, log, stop.Token), CancellationToken.None); }
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) { yield return envelope; }
        }
        finally { await stop.CancelAsync(); }
    }

    private static async Task Discover(IDigitalBrain brain, string brainId, LiveFeed feed, ChannelWriter<string> writer, ILogger log, CancellationToken ct)
    {
        try
        {
            await foreach (var source in feed.Sources(brain, brainId, ct))
            { _ = Task.Run(() => Pump(brain, feed, source, writer, log, ct), CancellationToken.None); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.LogWarning(error, "Live {Type} feed dropped: its sources failed.", feed.Type); }
    }

    private static async Task Pump(IDigitalBrain brain, LiveFeed feed, INeuron source, ChannelWriter<string> writer, ILogger log, CancellationToken ct)
    {
        try
        {
            // Retention is read once the watch is registered, so nothing published in between is missed.
            await foreach (var signal in feed.Watch(brain, source, () => Replay(feed, source, writer, log, ct), ct))
            { await writer.WriteAsync(Envelope(signal), ct); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.LogWarning(error, "Live {Type} source {Source} dropped.", feed.Type, source.GetPrimaryKeyString()); }
    }

    private static async Task Replay(LiveFeed feed, INeuron source, ChannelWriter<string> writer, ILogger log, CancellationToken ct)
    {
        RetainedSignal[] retained;
        try { retained = await source.AsReference<IRetainsSignals>().Retained(feed.Type, 0).WaitAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogDebug(error, "Live {Type} source {Source} has no retained value.", feed.Type, source.GetPrimaryKeyString());
            return;
        }
        if (retained.LastOrDefault() is { } last) { await writer.WriteAsync(Envelope(last.Signal), ct); }
    }

    private static string Envelope(Signal signal) => JsonSerializer.Serialize(
        new { type = signal.GetType().Name, publisher = signal.Publisher.ToString(), payload = JsonSerializer.SerializeToElement(signal, signal.GetType(), Json) }, Json);
}
