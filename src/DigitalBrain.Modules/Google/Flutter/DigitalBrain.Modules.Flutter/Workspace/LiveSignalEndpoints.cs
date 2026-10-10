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
using Orleans;

namespace DigitalBrain.Flutter.Workspace;

// One server-sent stream of the signals a shell screen renders live: GET /brains/{id}/live?type=A&type=B.
// Each event is {"type","publisher","payload"}. A source's last retained signal of the type is sent first,
// so a screen opened late starts from the current state instead of waiting for the next change.
internal static class LiveSignalEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    internal sealed record LiveFeed(
        string Type,
        Func<IDigitalBrain, string, CancellationToken, Task<INeuron[]>> Sources,
        Func<IDigitalBrain, INeuron, Func<Task>, CancellationToken, IAsyncEnumerable<Signal>> Watch);

    internal static LiveFeed Feed<T>(
        Func<IDigitalBrain, string, CancellationToken, Task<INeuron[]>> sources) where T : Signal
        => new(typeof(T).Name, sources, (brain, source, subscribed, ct) => brain.On<T>(source, subscribed, ct));

    internal static readonly LiveFeed[] Feeds =
    [
        Feed<ClusterChanged>((brain, brainId, _) => Task.FromResult<INeuron[]>([brain.Get<ICluster>(ICluster.Key(brainId))])),
        Feed<AppActivity>(async (brain, brainId, ct) =>
        {
            List<INeuron> apps = [];
            await foreach (var instance in brain.Kernel.Instances<IApp>(ct)) { if (IApp.NameOf(brainId, instance.Key) is not null) { apps.Add(brain.Get<IApp>(instance.Key)); } }
            return [.. apps];
        }),
    ];

    public static void Map(IEndpointRouteBuilder endpoints)
        => BrainRoutes.Group(endpoints).MapGet("/live", Live);

    private static async Task Live(HttpContext http, IDigitalBrain brain, CancellationToken ct)
    {
        var requested = http.Request.Query["type"].Where(name => !string.IsNullOrEmpty(name)).Select(name => name!).Distinct().ToArray();
        var unknown = requested.Where(name => Feeds.All(feed => feed.Type != name)).ToArray();
        if (requested.Length == 0 || unknown.Length > 0)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = requested.Length == 0 ? "Name at least one signal type." : $"Not a live signal: {string.Join(", ", unknown)}." }, ct);
            return;
        }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.Body.FlushAsync(ct);
        await foreach (var envelope in Stream(brain, BrainScope.CurrentId(), [.. Feeds.Where(feed => requested.Contains(feed.Type))], ct))
        {
            await http.Response.WriteAsync("data: " + envelope + "\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    // The envelopes as JSON text, retained signals first per source, then live ones, until ct ends the stream.
    internal static async IAsyncEnumerable<string> Stream(IDigitalBrain brain, string brainId, LiveFeed[] feeds, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new List<Task>();
        foreach (var feed in feeds)
        {
            foreach (var source in await feed.Sources(brain, brainId, ct))
            {
                pumps.Add(Task.Run(() => Pump(brain, feed, source, channel.Writer, stop.Token), CancellationToken.None));
            }
        }
        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None);
        try { await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) { yield return envelope; } }
        finally { await stop.CancelAsync(); }
    }

    private static async Task Pump(IDigitalBrain brain, LiveFeed feed, INeuron source, ChannelWriter<string> writer, CancellationToken ct)
    {
        try
        {
            // Retention is read once the watch is registered, so nothing published in between is missed.
            await foreach (var signal in feed.Watch(brain, source, () => Replay(feed, source, writer, ct), ct))
            { await writer.WriteAsync(Envelope(signal), ct); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { writer.TryComplete(error); }
    }

    private static async Task Replay(LiveFeed feed, INeuron source, ChannelWriter<string> writer, CancellationToken ct)
    {
        RetainedSignal[] retained;
        try { retained = await source.AsReference<IRetainsSignals>().Retained(feed.Type, 0).WaitAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { return; }
        if (retained.LastOrDefault() is { } last) { await writer.WriteAsync(Envelope(last.Signal), ct); }
    }

    private static string Envelope(Signal signal) => JsonSerializer.Serialize(
        new { type = signal.GetType().Name, publisher = signal.Publisher.ToString(), payload = JsonSerializer.SerializeToElement(signal, signal.GetType(), Json) }, Json);
}
