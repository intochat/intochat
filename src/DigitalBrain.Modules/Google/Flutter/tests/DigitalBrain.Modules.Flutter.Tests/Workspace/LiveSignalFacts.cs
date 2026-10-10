using System.Text.Json;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using DigitalBrain.Testing.Module;
using Orleans.Concurrency;

namespace DigitalBrain.Modules.Flutter.Tests.Workspace;

public sealed class LiveSignalFacts
{
    private static LiveSignalEndpoints.LiveFeed Feed(string key) => LiveSignalEndpoints.Feed<Beat>((brain, _, _) => Task.FromResult<INeuron[]>([brain.Get<IBeater>(key)]));

    [Fact]
    public async Task APublishedSignalIsForwardedAsATypedEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var beater = brain.Get<IBeater>("beater-live");
        await using var stream = LiveSignalEndpoints.Stream(brain, "brain", [Feed("beater-live")], ct).GetAsyncEnumerator(ct);
        var next = stream.MoveNextAsync().AsTask();
        await beater.Beat(7);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal("Beat", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal("beater-live", envelope.RootElement.GetProperty("publisher").GetString());
        Assert.Equal(7, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Fact]
    public async Task ALateSubscriberStartsFromTheRetainedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var beater = brain.Get<IBeater>("beater-late");
        await beater.Beat(1);
        await beater.Beat(2);
        await using var stream = LiveSignalEndpoints.Stream(brain, "brain", [Feed("beater-late")], ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        using var envelope = JsonDocument.Parse(stream.Current);
        Assert.Equal(2, envelope.RootElement.GetProperty("payload").GetProperty("pulse").GetInt32());
    }

    [Fact]
    public void ClusterAndAppActivityAreTheLiveFeeds()
        => Assert.Equal(["ClusterChanged", "AppActivity"], LiveSignalEndpoints.Feeds.Select(feed => feed.Type));
}

[GenerateSerializer, Alias("test.beat")] public sealed record Beat([property: Id(0)] int Pulse) : Signal;

[Alias("test.beater"), Orleans.Metadata.DefaultGrainType("test.beater")]
public interface IBeater : INeuron { Task Beat(int pulse); }

[GrainType("test.beater")]
public sealed class Beater : Neuron, IBeater, IRetainsSignals
{
    private Beat? last;
    public async Task Beat(int pulse) { last = new Beat(pulse) { Publisher = this.GetPrimaryKeyString() }; await PublishAsync(new Beat(pulse)); }
    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(Beat) && last is not null ? [new RetainedSignal(1, last)] : []);
}
