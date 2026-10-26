using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Circles.Signals;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Diagnostics;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Orleans;

namespace IntoChat.OS.Modules.Flutter.Workspace;

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
        Func<IIntoChatOS, string, Action, CancellationToken, IAsyncEnumerable<INeuron>> Sources,
        Func<IIntoChatOS, INeuron, Func<Task>, CancellationToken, IAsyncEnumerable<Signal>> Watch)
    {
        // Set for a feed of a plain publisher (06-07b): its sources are runtime ids, watched on the session synapse neuron.
        public PlainSpec? Plain { get; init; }
    }

    // Transitional (06-07b, R4-17; folded into LiveFeed in 06-08): the [Signal] record a plain feed carries and its sources.
    internal sealed record PlainSpec(Type Signal, Func<IIntoChatOS, string, Action, CancellationToken, IAsyncEnumerable<global::DigitalBrain.NeuronId>> Sources);

    internal static LiveFeed PlainFeed<T>(
        Func<IIntoChatOS, string, Action, CancellationToken, IAsyncEnumerable<global::DigitalBrain.NeuronId>> sources) where T : global::DigitalBrain.Signal
        => new(typeof(T).Name, static (_, _, listed, _) => None(listed), static (_, _, _, _) => throw new NotSupportedException("A plain feed has no grain sources."))
        { Plain = new(typeof(T), sources) };

    private static async IAsyncEnumerable<INeuron> None(Action listed) { await Task.CompletedTask; listed(); yield break; }

    internal static LiveFeed Feed<T>(
        Func<IIntoChatOS, string, Action, CancellationToken, IAsyncEnumerable<INeuron>> sources) where T : Signal
        => new(typeof(T).Name, sources, (brain, source, subscribed, ct) => brain.On<T>(source, subscribed, ct));

    internal static readonly LiveFeed[] Feeds =
    [
        Feed<DeploymentChanged>(static (brain, brainId, _, _) => Single(brain.Get<IDeployment>(IDeployment.Key(brainId)))),
        // Apps are plain neurons (06-08): their activity is watched on the session synapse neuron.
        PlainFeed<AppActivity>(Apps),
        // R126: asked of a circle brain's own /live; the shell watches each circle the person is in.
        Feed<CircleChanged>(static (brain, brainId, _, _) => Single(brain.Get<ICircle>(ICircle.Key(brainId)))),
        Feed<CircleSpoke>(static (brain, brainId, _, _) => Single(brain.Get<ICircleChat>(ICircleChat.Key(brainId)))),
    ];

    private static async IAsyncEnumerable<INeuron> Single(INeuron neuron) { await Task.CompletedTask; yield return neuron; }

    // App keys waiting for the stream. A dropped key would leave an app out of the watch for good, so a full
    // buffer holds the kernel enumeration back instead of dropping.
    internal const int KeyBuffer = 256;

    // Apps already there, then each one the kernel announces: the watch is registered before the listing, so none is missed.
    // A null key marks the end of the listing. A key waiting in the buffer is not queued again, so the dedup set
    // holds at most the buffer's keys; once read, `seen` keeps the stream from yielding it twice.
    private static async IAsyncEnumerable<global::DigitalBrain.NeuronId> Apps(IIntoChatOS brain, string brainId, Action listed, [EnumeratorCancellation] CancellationToken ct)
    {
        var keys = Channel.CreateBounded<string?>(new BoundedChannelOptions(KeyBuffer) { FullMode = BoundedChannelFullMode.Wait });
        HashSet<string> queued = [];
        async Task Queue(string key)
        {
            lock (queued) { if (!queued.Add(key)) { return; } }
            await keys.Writer.WriteAsync(key, ct);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var created in brain.On<InstanceCreated>(brain.Self, async () =>
                {
                    await foreach (var instance in brain.Kernel.Instances<IApp>(ct)) { await Queue(instance.Key); }
                    await keys.Writer.WriteAsync(null, ct);
                }, ct)) { await Queue(created.Key); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { keys.Writer.TryComplete(error); }
        }, CancellationToken.None);
        HashSet<string> seen = [];
        await foreach (var key in keys.Reader.ReadAllAsync(ct))
        {
            if (key is not null) { lock (queued) { queued.Remove(key); } }
            if (key is null) { listed(); }
            else if (IApp.NameOf(brainId, key) is not null && seen.Add(key))
            { yield return new(new global::DigitalBrain.BrainId(brainId), IntoChat.OS.Connections.AppGrainType.Name, key[(brainId.Length + 1)..]); }
        }
    }

    public static void Map(IEndpointRouteBuilder endpoints)
        => BrainRoutes.Group(endpoints).MapGet("/live", Live);

    private static async Task Live(HttpContext http, IIntoChatOS brain, ILoggerFactory logs, CancellationToken ct)
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
        var plain = http.RequestServices.GetService(typeof(IntoChat.OS.Runtime.IPlainSignals)) as IntoChat.OS.Runtime.IPlainSignals;
        await foreach (var envelope in Stream(brain, BrainScope.CurrentId(), selected, logs.CreateLogger("IntoChat.OS.Modules.Flutter.Live"), ct, plain: plain))
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

    // Sent instead of what a stalled reader would have missed; the stream then ends and the client reconnects and reloads.
    internal const string Resync = "{\"type\":\"resync\"}";

    // Envelopes held for a reader that is not keeping up; one more means resync.
    internal const int EnvelopeBuffer = 1024;

    // A bounded outbox that never blocks a pump: an envelope that does not fit marks the stream overflowed and ends it.
    private sealed class Outbox(int capacity)
    {
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
        private int _overflowed;

        public bool Overflowed => Volatile.Read(ref _overflowed) == 1;
        public ChannelReader<string> Reader => _channel.Reader;

        public ValueTask Write(string envelope, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_channel.Writer.TryWrite(envelope) || Overflowed) { return ValueTask.CompletedTask; }
            if (Interlocked.Exchange(ref _overflowed, 1) == 0) { _channel.Writer.TryComplete(); }
            return ValueTask.CompletedTask;
        }

        public void Fail(Exception error) => _channel.Writer.TryComplete(error);
    }

    // The envelopes as JSON text, retained signals first per source, then Ready, then live ones, until ct ends the stream.
    // The stream stays open with no sources at all: a source may appear later. A source or feed that fails is logged and dropped;
    // when every feed has failed the stream ends with an error, so the client sees a failure instead of a silent hang.
    // A reader that falls more than `capacity` envelopes behind gets Resync and the stream ends.
    internal static async IAsyncEnumerable<string> Stream(IIntoChatOS brain, string brainId, LiveFeed[] feeds, ILogger log, [EnumeratorCancellation] CancellationToken ct, int capacity = EnvelopeBuffer,
        IntoChat.OS.Runtime.IPlainSignals? plain = null)
    {
        var outbox = new Outbox(capacity);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var failed = 0;
            void Failed()
            {
                if (Interlocked.Increment(ref failed) == feeds.Length)
                { outbox.Fail(new InvalidOperationException("Every live feed failed; see the server log.")); }
            }
            var replayed = feeds.Select(feed => Task.Run(() => feed.Plain is { } spec
                ? DiscoverPlain(brain, brainId, feed.Type, spec, plain, outbox, log, Failed, stop.Token)
                : Discover(brain, brainId, feed, outbox, log, Failed, stop.Token), CancellationToken.None)).ToArray();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(replayed);
                    await outbox.Write(Ready, stop.Token);
                }
                catch (Exception closed) when (closed is OperationCanceledException or ChannelClosedException) { log.LogSwallowed(closed, "live stream closed before ready"); }
            }, CancellationToken.None);
            while (await outbox.Reader.WaitToReadAsync(ct))
            {
                if (outbox.Overflowed) { break; }
                if (outbox.Reader.TryRead(out var envelope)) { yield return envelope; }
            }
            if (outbox.Overflowed)
            {
                log.LogInformation("Live stream for {Brain} overflowed {Capacity} envelopes; the reader resyncs.", brainId, capacity);
                yield return Resync;
            }
        }
        finally { await stop.CancelAsync(); }
    }

    // Completes once the retained values of the sources listed at connect time are written (or the feed failed);
    // discovery keeps running after that.
    private static Task Discover(IIntoChatOS brain, string brainId, LiveFeed feed, Outbox writer, ILogger log, Action failed, CancellationToken ct)
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

    // Discover for a plain feed: the same listing and replay contract, each source on its own session (06-07b).
    private static Task DiscoverPlain(IIntoChatOS brain, string brainId, string type, PlainSpec spec, IntoChat.OS.Runtime.IPlainSignals? plain, Outbox writer, ILogger log, Action failed, CancellationToken ct)
    {
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (plain is null)
        {
            log.LogWarning("Live {Type} feed dropped: this host registers no IPlainSignals.", type);
            failed();
            replayed.TrySetResult();
            return replayed.Task;
        }
        var alias = IntoChat.OS.Runtime.PlainSignalTypes.AliasOf(spec.Signal);
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
                await foreach (var source in spec.Sources(brain, brainId, () => { listing = false; Listed(); }, ct))
                {
                    var replay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (listing) { lock (initial) { initial.Add(replay.Task); } }
                    _ = Task.Run(() => PlainPump(plain, spec.Signal, alias, source, writer, log, replay, ct), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { log.LogWarning(error, "Live {Type} feed dropped: its sources failed.", type); failed(); }
            finally { if (listing) { listing = false; Listed(); } }
        }, CancellationToken.None);
        return replayed.Task;
    }

    private static async Task PlainPump(IntoChat.OS.Runtime.IPlainSignals plain, Type signal, string alias, global::DigitalBrain.NeuronId source, Outbox writer, ILogger log, TaskCompletionSource replayed, CancellationToken ct)
    {
        try
        {
            await foreach (var entry in IntoChat.OS.Runtime.PlainWatch.Live(plain, source, [alias], "live/" + Guid.NewGuid().ToString("N"), async () =>
            {
                try
                {
                    if (await IntoChat.OS.Runtime.PlainWatch.Retained(plain, source, alias, ct) is { } last) { await writer.Write(PlainEnvelope(signal, source, last, replay: true), ct); }
                }
                finally { replayed.TrySetResult(); }
            }, ct))
            {
                if (!entry.IsGap && entry.Alias == alias) { await writer.Write(PlainEnvelope(signal, source, entry), ct); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.LogWarning(error, "Live {Type} source {Source} dropped.", signal.Name, source); }
        finally { replayed.TrySetResult(); }
    }

    internal static string PlainEnvelope(Type signal, global::DigitalBrain.NeuronId source, IntoChat.OS.Runtime.PlainSignalEntry entry, bool replay = false) => JsonSerializer.Serialize(
        new
        {
            type = signal.Name,
            publisher = source.ToString(),
            sequence = (long?)entry.Sequence,
            replay = replay ? true : (bool?)null,
            payload = JsonSerializer.SerializeToElement(JsonSerializer.Deserialize(entry.Json, signal, JsonSerializerOptions.Web), signal, Json),
        }, Json);

    private static async Task Pump(IIntoChatOS brain, LiveFeed feed, INeuron source, Outbox writer, ILogger log, TaskCompletionSource replayed, CancellationToken ct)
    {
        try
        {
            // Retention is read once the watch is registered, so nothing published in between is missed.
            await foreach (var signal in feed.Watch(brain, source, async () =>
            {
                try { await Replay(feed, source, writer, log, ct); }
                finally { replayed.TrySetResult(); }
            }, ct))
            { await writer.Write(Envelope(signal, await SequenceOf(source, feed.Type, signal, ct)), ct); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.LogWarning(error, "Live {Type} source {Source} dropped.", feed.Type, source.GetPrimaryKeyString()); }
        finally { replayed.TrySetResult(); }
    }

    private static async Task Replay(LiveFeed feed, INeuron source, Outbox writer, ILogger log, CancellationToken ct)
    {
        RetainedSignal[] retained;
        try { retained = await source.AsReference<IRetainsSignals>().Retained(feed.Type, 0).WaitAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogDebug(error, "Live {Type} source {Source} has no retained value.", feed.Type, source.GetPrimaryKeyString());
            return;
        }
        if (retained.LastOrDefault() is { } last) { await writer.Write(Envelope(last.Signal, last.Sequence, replay: true), ct); }
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
