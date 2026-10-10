using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Circles;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Flutter.Tests.Workspace;

public sealed class LiveSignalFacts
{
    private static async IAsyncEnumerable<INeuron> Keys(IDigitalBrain brain, string[] now, Channel<string>? later, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var key in now) { yield return brain.Get<IBeater>(key); }
        if (later is null) { yield break; }
        await foreach (var key in later.Reader.ReadAllAsync(ct)) { yield return brain.Get<IBeater>(key); }
    }

    private static LiveSignalEndpoints.LiveFeed Feed(string key) => Feed([key], null);

    private static LiveSignalEndpoints.LiveFeed Feed(string[] now, Channel<string>? later)
        => LiveSignalEndpoints.Feed<Beat>((brain, _, ct) => Keys(brain, now, later, ct));

    private static IAsyncEnumerator<string> Open(ModuleBrain brain, LiveSignalEndpoints.LiveFeed[] feeds, CancellationToken ct)
        => LiveSignalEndpoints.Stream(brain, "brain", feeds, NullLogger.Instance, ct).GetAsyncEnumerator(ct);

    private static async Task<JsonDocument> Next(IAsyncEnumerator<string> stream, CancellationToken ct)
    {
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        return JsonDocument.Parse(stream.Current);
    }

    [Fact]
    public async Task APublishedSignalIsForwardedAsATypedEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var beater = brain.Get<IBeater>("beater-live");
        await using var stream = Open(brain, [Feed("beater-live")], ct);
        var next = stream.MoveNextAsync().AsTask();
        await beater.Beat(7);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal("Beat", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal("beater-live", envelope.RootElement.GetProperty("publisher").GetString());
        Assert.Equal(7, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    private static void Act(string brain) => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = brain,
        AccountId = "acct-" + brain,
        BrainId = brain,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    });

    private static async Task<ModuleBrain> CirclesBrain(CancellationToken ct)
    {
        var brain = await ModuleTest.Create().WithModule<CirclesModule>().StartAsync(ct);
        foreach (var name in new[] { "alice", "bob" }) { await brain.Get<IBrain>(name).Create(new Owner("acct-" + name)); }
        return brain;
    }

    private static LiveSignalEndpoints.LiveFeed CircleFeed => LiveSignalEndpoints.Feeds.Single(feed => feed.Type == "CircleTouched");

    [Fact]
    public async Task ACircleTouchedOnTheBrainsIndexArrivesAsAnEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await CirclesBrain(ct);
        await using var stream = LiveSignalEndpoints.Stream(brain, "bob", [CircleFeed], NullLogger.Instance, ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        Act("alice");
        await brain.Get<ICircle>(ICircle.Key("alice", "team")).Invite("bob");
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal("CircleTouched", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal("circles.index/bob/circles", envelope.RootElement.GetProperty("publisher").GetString());
        Assert.Equal("invited", envelope.RootElement.GetProperty("payload").GetProperty("kind").GetString());
        Assert.Equal(1, envelope.RootElement.GetProperty("sequence").GetInt64());
        Assert.False(envelope.RootElement.TryGetProperty("replay", out var replay) && replay.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task ALateCircleSubscriberIsSeededFromTheIndexRetention()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await CirclesBrain(ct);
        Act("alice");
        await brain.Get<ICircle>(ICircle.Key("alice", "team")).Invite("bob");
        await using var stream = LiveSignalEndpoints.Stream(brain, "bob", [CircleFeed], NullLogger.Instance, ct).GetAsyncEnumerator(ct);
        using var envelope = await Next(stream, ct);
        Assert.Equal("team", envelope.RootElement.GetProperty("payload").GetProperty("name").GetString());
        Assert.Equal("invited", envelope.RootElement.GetProperty("payload").GetProperty("kind").GetString());
        Assert.Equal(1, envelope.RootElement.GetProperty("sequence").GetInt64());
        Assert.True(envelope.RootElement.GetProperty("replay").GetBoolean());
    }

    [Fact]
    public async Task ASourceWithoutRetentionSendsNoSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var beater = brain.Get<IBeater>("beater-plain");
        await using var stream = Open(brain, [Feed("beater-plain")], ct);
        var next = stream.MoveNextAsync().AsTask();
        await beater.BeatUnretained(1);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("sequence").ValueKind);
    }

    [Fact]
    public async Task ALateSubscriberStartsFromTheRetainedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var beater = brain.Get<IBeater>("beater-late");
        await beater.Beat(1);
        await beater.Beat(2);
        await using var stream = Open(brain, [Feed("beater-late")], ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal(2, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Fact]
    public async Task ASourceThatAppearsLaterIsStreamedAndNoSourcesKeepsTheStreamOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var later = Channel.CreateUnbounded<string>();
        await using var stream = Open(brain, [Feed([], later)], ct);
        var next = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        Assert.False(next.IsCompleted);
        later.Writer.TryWrite("beater-new");
        await Task.Delay(300, ct);
        await brain.Get<IBeater>("beater-new").Beat(5);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal(5, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Fact]
    public async Task AFailingFeedIsDroppedWithoutEndingTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var broken = LiveSignalEndpoints.Feed<Beat>((_, _, _) => throw new InvalidOperationException("no sources"));
        await brain.Get<IBeater>("beater-ok").Beat(3);
        await using var stream = Open(brain, [broken, Feed("beater-ok")], ct);
        using var envelope = await Next(stream, ct);
        Assert.Equal(3, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Fact]
    public async Task CancellingTheRequestEndsTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var stream = Open(brain, [Feed("beater-gone")], disconnect.Token);
        var next = stream.MoveNextAsync().AsTask();
        await Task.Delay(300, ct);
        await disconnect.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public void AnUnknownOrMissingTypeIsRefused()
    {
        Assert.NotNull(LiveSignalEndpoints.Select([]).Error);
        Assert.Contains("Nope", LiveSignalEndpoints.Select(["ClusterChanged", "Nope"]).Error);
        Assert.Null(LiveSignalEndpoints.Select(["ClusterChanged", "ClusterChanged"]).Error);
        Assert.Single(LiveSignalEndpoints.Select(["ClusterChanged", "ClusterChanged"]).Feeds);
    }

    [Fact]
    public void ClusterAppActivityAndCircleTouchedAreTheLiveFeeds()
        => Assert.Equal(["ClusterChanged", "AppActivity", "CircleTouched"], LiveSignalEndpoints.Feeds.Select(feed => feed.Type));
}

[GenerateSerializer, Alias("test.beat")] public sealed record Beat([property: Id(0)] int Pulse) : Signal;

[Alias("test.beater"), Orleans.Metadata.DefaultGrainType("test.beater")]
public interface IBeater : INeuron { Task Beat(int pulse); Task BeatUnretained(int pulse); }

[GrainType("test.beater")]
public sealed class Beater : Neuron, IBeater, IRetainsSignals
{
    private Beat? last;
    public async Task Beat(int pulse) { last = new Beat(pulse) { Publisher = this.GetPrimaryKeyString() }; await PublishAsync(new Beat(pulse)); }
    public Task BeatUnretained(int pulse) => PublishAsync(new Beat(pulse));
    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(Beat) && last is not null ? [new RetainedSignal(1, last)] : []);
}
