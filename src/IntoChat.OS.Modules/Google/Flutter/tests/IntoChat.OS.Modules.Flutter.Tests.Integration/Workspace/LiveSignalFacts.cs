using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

public sealed class LiveSignalFacts
{
    internal const string BeatAlias = "test.beat";

    // The beaters of the owner's brain, by key.
    private static DigitalBrain.NeuronId Id(string key) => new(new DigitalBrain.BrainId("owner"), Beater.Type, key);

    private static async IAsyncEnumerable<DigitalBrain.NeuronId> Keys(string[] now, Channel<string>? later, Action listed, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var key in now) { yield return Id(key); }
        listed();
        if (later is null) { yield break; }
        await foreach (var key in later.Reader.ReadAllAsync(ct)) { yield return Id(key); }
    }

    private static LiveSignalEndpoints.LiveFeed Feed(string key) => Feed([key], null);

    private static LiveSignalEndpoints.LiveFeed Feed(string[] now, Channel<string>? later)
        => LiveSignalEndpoints.Feed<Beat>((_, _, listed, ct) => Keys(now, later, listed, ct));

    private static IAsyncEnumerator<string> Open(ITestBrain brain, LiveSignalEndpoints.LiveFeed[] feeds, CancellationToken ct)
        => LiveSignalEndpoints.Stream(brain, "owner", feeds, NullLogger.Instance, ct, plain: brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainSignals>()).GetAsyncEnumerator(ct);

    private static IBeater BeaterOf(ITestBrain brain, string key) => brain.Get<IBeater>("owner/" + key);

    private static Task<ITestBrain> Start(CancellationToken ct) => TestBrain.Create().WithNeuronModule<BeaterNeurons>().StartAsync(ct);

    private static async Task<JsonDocument> Next(IAsyncEnumerator<string> stream, CancellationToken ct)
    {
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        return JsonDocument.Parse(stream.Current);
    }

    // The next envelope after the ready marker: the stream is connected and replayed, so what follows is live. A plain source's
    // retained last signal (a circle's founding CircleChanged, 06-08) is replayed before the marker.
    private static async Task<Task<bool>> AfterReady(IAsyncEnumerator<string> stream, CancellationToken ct)
    {
        do { Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct)); }
        while (stream.Current != LiveSignalEndpoints.Ready);
        return stream.MoveNextAsync().AsTask();
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APublishedSignalIsForwardedAsATypedEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var beater = BeaterOf(brain, "beater-live");
        await using var stream = Open(brain, [Feed("beater-live")], ct);
        var next = await AfterReady(stream, ct);
        await beater.Beat(7);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal("Beat", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal("owner/test.beater/beater-live", envelope.RootElement.GetProperty("publisher").GetString());
        Assert.Equal(7, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    // A host whose circles are brains (T26-1): the Apps module over the features folder gives a circle brain its room.
    private static Task<ITestBrain> CirclesBrain(CancellationToken ct) => TestBrain.Create()
        .WithModule<IntoChat.OS.Modules.Apps.AppsModule, IntoChat.OS.Modules.Apps.AppsOptions>(options => options.Features = Features)
        .WithModule<CirclesModule>()
        .ConfigureServices(services =>
        {
            MicrosoftSdk.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IntoChat.OS.Modules.Apps.IAppRuntime>(services);
            MicrosoftSdk.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IntoChat.OS.Modules.Apps.IAppRuntime, NoRuntime>(services);
        })
        .StartAsync(ct);

    private static readonly string Features = Path.Combine(Root(), "features");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Intochat.OS.slnx was not found.");
    }

    private sealed class NoRuntime : IntoChat.OS.Modules.Apps.IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Alice creates a circle through her factory; it is a brain of its own.
    private static async Task<string> Circle(ITestBrain brain, IntoChat.OS.Testing.Person alice)
    {
        using (alice.AsCaller()) { return await brain.Get<ICircleFactory>(ICircleFactory.Key(alice.BrainId)).Create("team"); }
    }

    private static LiveSignalEndpoints.LiveFeed SpokeFeed => LiveSignalEndpoints.Feeds.Single(feed => feed.Type == "CircleSpoke");

    private static LiveSignalEndpoints.LiveFeed ChangedFeed => LiveSignalEndpoints.Feeds.Single(feed => feed.Type == "CircleChanged");

    // R126: the shell watches each circle brain's own /live; a line said there arrives from the circle's chat.
    [Trait("host", "own")]
    [Fact]
    public async Task ALineSaidInACircleArrivesOnTheCircleBrainsLiveFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await CirclesBrain(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var circle = await Circle(brain, alice);
        await using var stream = LiveSignalEndpoints.Stream(brain, circle, [SpokeFeed], NullLogger.Instance, ct, plain: brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainSignals>()).GetAsyncEnumerator(ct);
        var next = await AfterReady(stream, ct);
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircleChat>(ICircleChat.Key(circle)).Say("hi"); }
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal("CircleSpoke", envelope.RootElement.GetProperty("type").GetString());
        // A plain publisher is named by its runtime id "brain/type/key" (06-08).
        Assert.Equal($"{circle}/circles.chat/chat", envelope.RootElement.GetProperty("publisher").GetString());
        Assert.Equal(("hi", alice.AccountId), (envelope.RootElement.GetProperty("payload").GetProperty("text").GetString(),
            envelope.RootElement.GetProperty("payload").GetProperty("speaker").GetString()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnInvitationChangesTheCircleOnItsLiveFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await CirclesBrain(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await Circle(brain, alice);
        await using var stream = LiveSignalEndpoints.Stream(brain, circle, [ChangedFeed], NullLogger.Instance, ct, plain: brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainSignals>()).GetAsyncEnumerator(ct);
        var next = await AfterReady(stream, ct);
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircle>(ICircle.Key(circle)).Invite(bob.AccountId); }
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal("CircleChanged", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal("team", envelope.RootElement.GetProperty("payload").GetProperty("name").GetString());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ALateSubscriberStartsFromTheRetainedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var beater = BeaterOf(brain, "beater-late");
        await beater.Beat(1);
        await beater.Beat(2);
        await using var stream = Open(brain, [Feed("beater-late")], ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal(2, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASourceThatAppearsLaterIsStreamedAndNoSourcesKeepsTheStreamOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var later = Channel.CreateUnbounded<string>();
        await using var stream = Open(brain, [Feed([], later)], ct);
        var next = await AfterReady(stream, ct);
        Assert.False(next.IsCompleted);
        // The beater retains its beat: whether the late source attaches before or after it, the beat arrives.
        await BeaterOf(brain, "beater-new").Beat(5);
        later.Writer.TryWrite("beater-new");
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal(5, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailingFeedIsDroppedWithoutEndingTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var broken = LiveSignalEndpoints.Feed<Beat>((_, _, _, _) => throw new InvalidOperationException("no sources"));
        await BeaterOf(brain, "beater-ok").Beat(3);
        await using var stream = Open(brain, [broken, Feed("beater-ok")], ct);
        using var envelope = await Next(stream, ct);
        Assert.Equal(3, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStreamWhoseEveryFeedFailedEndsWithAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var broken = LiveSignalEndpoints.Feed<Beat>((_, _, _, _) => throw new InvalidOperationException("no sources"));
        await using var stream = Open(brain, [broken], ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReadyFollowsTheReplayOfEverySelectedFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        await BeaterOf(brain, "beater-ready").Beat(4);
        await BeaterOf(brain, "beater-ready-2").Beat(5);
        await using var stream = Open(brain, [Feed("beater-ready"), Feed("beater-ready-2")], ct);
        using var first = await Next(stream, ct);
        using var second = await Next(stream, ct);
        Assert.Equal([4, 5], new[] { first, second }.Select(e => e.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32()).Order());
        using var ready = await Next(stream, ct);
        Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ALateSubscriberGetsTheReplayThenReadyThenLiveSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var beater = BeaterOf(brain, "beater-late-ready");
        await beater.Beat(1);
        await using var stream = Open(brain, [Feed("beater-late-ready")], ct);
        using var replayed = await Next(stream, ct);
        Assert.True(replayed.RootElement.GetProperty("replay").GetBoolean());
        var next = await AfterReady(stream, ct);
        await beater.Beat(2);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var live = JsonDocument.Parse(stream.Current);
        Assert.Equal(2, live.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFeedWithNoSourcesYetIsReadyAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        await using var stream = Open(brain, [Feed([], Channel.CreateUnbounded<string>())], ct);
        using var ready = await Next(stream, ct);
        Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CancellingTheRequestEndsTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var stream = Open(brain, [Feed("beater-gone")], disconnect.Token);
        var next = await AfterReady(stream, ct);
        await disconnect.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    // A feed whose one source publishes `count` beats as soon as it is watched (the feed's Live seam stands in for its session).
    private static LiveSignalEndpoints.LiveFeed Burst(string key, int count, TaskCompletionSource published)
        => LiveSignalEndpoints.Feed<Beat>((_, _, listed, ct) => Keys([key], null, listed, ct)) with
        { Live = (source, subscribed, ct) => Beats(source, subscribed, count, published, ct) };

    private static async IAsyncEnumerable<IntoChat.OS.Runtime.PlainSignalEntry> Beats(DigitalBrain.NeuronId source, Func<Task> subscribed, int count,
        TaskCompletionSource published, [EnumeratorCancellation] CancellationToken ct)
    {
        await subscribed();
        for (var pulse = 0; pulse < count; pulse++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new(source.ToString(), pulse + 1, BeatAlias, JsonSerializer.Serialize(new { pulse }), DateTimeOffset.UtcNow);
        }
        published.TrySetResult();
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStalledReaderGetsResyncAndTheStreamEndsInsteadOfBufferingWithoutBound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = LiveSignalEndpoints.Stream(brain, "owner", [Burst("beater-flood", 100, published)], NullLogger.Instance, ct, capacity: 8,
            plain: brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainSignals>()).GetAsyncEnumerator(ct);
        // The reader stalls while 100 envelopes are published into a buffer of 8.
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        List<string> received = [stream.Current];
        while (await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct)) { received.Add(stream.Current); }
        Assert.Equal(LiveSignalEndpoints.Resync, received[^1]);
        // The pumps may overflow before the first read, so Resync can be the only envelope; at most the buffer drains first.
        Assert.InRange(received.Count, 1, 8 + 2);
    }

    [Fact]
    public void TheLiveBuffersAreBounded()
    {
        Assert.Equal(1024, LiveSignalEndpoints.EnvelopeBuffer);
        Assert.Equal(256, LiveSignalEndpoints.KeyBuffer);
    }

    [Fact]
    public void AnUnknownOrMissingTypeIsRefused()
    {
        Assert.NotNull(LiveSignalEndpoints.Select([]).Error);
        Assert.Contains("Nope", LiveSignalEndpoints.Select(["DeploymentChanged", "Nope"]).Error);
        Assert.Null(LiveSignalEndpoints.Select(["DeploymentChanged", "DeploymentChanged"]).Error);
        Assert.Single(LiveSignalEndpoints.Select(["DeploymentChanged", "DeploymentChanged"]).Feeds);
    }

    [Fact]
    public void ClusterAppActivityAndTheCircleBrainsSignalsAreTheLiveFeeds()
        => Assert.Equal(["DeploymentChanged", "AppActivity", "CircleChanged", "CircleSpoke"], LiveSignalEndpoints.Feeds.Select(feed => feed.Type));
}

[DigitalBrain.Signal(LiveSignalFacts.BeatAlias)]
public sealed record Beat(int Pulse) : DigitalBrain.Signal;

[DigitalBrain.Contracts.NeuronAlias("test.beater"), DigitalBrain.Contracts.DefaultNeuronType(Beater.Type)]
public interface IBeater { Task Beat(int pulse); }

// A plain neuron (06-08): its last Beat is retained in its brain's log, which the live stream replays.
[DigitalBrain.Neuron(Type)]
public sealed class Beater : DigitalBrain.Neuron, IBeater
{
    public const string Type = "test.beater";

    public Task Beat(int pulse) => Publish(new Beat(pulse));
}

[DigitalBrain.Module("test.beater")]
public sealed class BeaterNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
