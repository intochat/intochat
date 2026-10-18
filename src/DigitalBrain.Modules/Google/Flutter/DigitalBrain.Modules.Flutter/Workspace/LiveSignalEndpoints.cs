using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Circles.Signals;
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
// so a screen opened late starts from the current state instead of waiting for the next change. Once the
// retained values of every source there at connect time are written, one {"type":"ready"} event follows:
// the client knows it is connected and the replay is complete.
internal static class LiveSignalEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    // Sources yields the neurons that publish the type: those that exist now, then any that appear later.
    // It calls listed once the ones that exist now are yielded; ending the sequence counts as listed too.
    internal sealed record LiveFeed(
        string Type,
        Func<IDigitalBrain, string, Action, CancellationToken, IAsyncEnumerable<INeuron>> Sources,
        Func<IDigitalBrain, INeuron, Func<Task>, CancellationToken, IAsyncEnumerable<Signal>> Watch);

    internal static LiveFeed Feed<T>(
        Func<IDigitalBrain, string, Action, CancellationToken, IAsyncEnumerable<INeuron>> sources) where T : Signal
        => new(typeof(T).Name, sources, (brain, source, subscribed, ct) => brain.On<T>(source, subscribed, ct));

    internal static readonly LiveFeed[] Feeds =
    [
        Feed<DeploymentChanged>(static (brain, brainId, _, _) => Single(brain.Get<IDeployment>(IDeployment.Key(brainId)))),
        Feed<AppActivity>(Apps),
        // R126: asked of a circle brain's own /live; the shell watches each circle the person is in.
        Feed<CircleChanged>(static (brain, brainId, _, _) => Single(brain.Get<ICircle>(ICircle.Key(brainId)))),
        Feed<CircleSpoke>(static (brain, brainId, _, _) => Single(brain.Get<ICircleChat>(ICircleChat.Key(brainId)))),
    ];

    private static async IAsyncEnumerable<INeuron> Single(INeuron neuron) { await Task.CompletedTask; yield return neuron; }

    // Apps already there, then each one the kernel announces: the watch is registered before the listing, so none is missed.
    // A null key marks the end of the listing.
    private static async IAsyncEnumerable<INeuron> Apps(IDigitalBrain brain, string brainId, Action listed, [EnumeratorCancellation] CancellationToken ct)
    {
        var keys = Channel.CreateUnbounded<string?>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var created in brain.On<InstanceCreated>(brain.Self, async () =>
                {
                    await foreach (var instance in brain.Kernel.Instances<IApp>(ct)) { keys.Writer.TryWrite(instance.Key); }
                    keys.Writer.TryWrite(null);
                }, ct)) { keys.Writer.TryWrite(created.Key); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { keys.Writer.TryComplete(error); }
        }, CancellationToken.None);
        HashSet<string> seen = [];
        await foreach (var key in keys.Reader.ReadAllAsync(ct))
        {
            if (key is null) { listed(); }
            else if (IApp.NameOf(brainId, key) is not null && seen.Add(key)) { yield return brain.Get<IApp>(key); }
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

    internal const string Ready = "{\"type\":\"ready\"}";

    // The envelopes as JSON text, retained signals first per source, then Ready, then live ones, until ct ends the stream.
    // The stream stays open with no sources at all: a source may appear later. A source or feed that fails is logged and dropped;
    // when every feed has failed the stream ends with an error, so the client sees a failure instead of a silent hang.
    internal static async IAsyncEnumerable<string> Stream(IDigitalBrain brain, string brainId, LiveFeed[] feeds, ILogger log, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var failed = 0;
            void Failed()
            {
                if (Interlocked.Increment(ref failed) == feeds.Length)
                { channel.Writer.TryComplete(new InvalidOperationException("Every live feed failed; see the server log.")); }
            }
            var replayed = feeds.Select(feed => Task.Run(() => Discover(brain, brainId, feed, channel.Writer, log, Failed, stop.Token), CancellationToken.None)).ToArray();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(replayed);
                    await channel.Writer.WriteAsync(Ready, stop.Token);
                }
                catch (Exception closed) when (closed is OperationCanceledException or ChannelClosedException) { }
            }, CancellationToken.None);
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) { yield return envelope; }
        }
        finally { await stop.CancelAsync(); }
    }

    // Completes once the retained values of the sources listed at connect time are written (or the feed failed);
    // discovery keeps running after that.
    private static Task Discover(IDigitalBrain brain, string brainId, LiveFeed feed, ChannelWriter<string> writer, ILogger log, Action failed, CancellationToken ct)
    {
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task> initial = [];
        void Listed()
        {
            Task[] pending;
            lock (initial) { if (replayed.Task.IsCompleted) { return; } pending = [.. initial]; }
            _ = Task.WhenAll(pending).ContinueWith(_ => replayed.TrySetResult(), TaskScheduler.Default);
        }
        _ = Task.Run(async () =>
        {
            var listing = true;
            try
            {
                await foreach (var source in feed.Sources(brain, brainId, () => { listing = false; Listed(); }, ct))
                {
                    var replay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (listing) { lock (initial) { initial.Add(replay.Task); } }
                    _ = Task.Run(() => Pump(brain, feed, source, writer, log, replay, ct), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { log.LogWarning(error, "Live {Type} feed dropped: its sources failed.", feed.Type); failed(); }
            finally { if (listing) { listing = false; Listed(); } }
        }, CancellationToken.None);
        return replayed.Task;
    }

    private static async Task Pump(IDigitalBrain brain, LiveFeed feed, INeuron source, ChannelWriter<string> writer, ILogger log, TaskCompletionSource replayed, CancellationToken ct)
    {
        try
        {
            // Retention is read once the watch is registered, so nothing published in between is missed.
            await foreach (var signal in feed.Watch(brain, source, async () =>
            {
                try { await Replay(feed, source, writer, log, ct); }
                finally { replayed.TrySetResult(); }
            }, ct))
            { await writer.WriteAsync(Envelope(signal, await SequenceOf(source, feed.Type, signal, ct)), ct); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.LogWarning(error, "Live {Type} source {Source} dropped.", feed.Type, source.GetPrimaryKeyString()); }
        finally { replayed.TrySetResult(); }
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
        if (retained.LastOrDefault() is { } last) { await writer.WriteAsync(Envelope(last.Signal, last.Sequence, replay: true), ct); }
    }

    // A live signal carries the sequence it was retained under, so a client can tell it from a replayed one.
    // A source that retains nothing, or has not retained this signal, sends none.
    private static async Task<long?> SequenceOf(INeuron source, string type, Signal signal, CancellationToken ct)
    {
        try
        {
            var retained = await source.AsReference<IRetainsSignals>().Retained(type, 0).WaitAsync(ct);
            var same = retained.LastOrDefault(entry => entry.Signal with { Trace = null } == signal with { Trace = null });
            return same?.Sequence;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    internal static string Envelope(Signal signal, long? sequence = null, bool replay = false) => JsonSerializer.Serialize(
        new
        {
            type = signal.GetType().Name,
            publisher = signal.Publisher.ToString(),
            sequence,
            replay = replay ? true : (bool?)null,
            payload = JsonSerializer.SerializeToElement(signal, signal.GetType(), Json),
        }, Json);
}
