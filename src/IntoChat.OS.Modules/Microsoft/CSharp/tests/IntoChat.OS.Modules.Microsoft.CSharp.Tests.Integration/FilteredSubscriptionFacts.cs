using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class FilteredSubscriptionFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AddingASourceDoesNotRewatchUnchangedSources()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var edge = RuntimeBrain.Edge(brain, "acc-brain");
        var runtime = RuntimeBrain.Runtime(brain, "acc-brain");
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var sources = Enumerable.Range(0, 8).Select(i => brain.Get<IExpansionSource>("acc-brain/source-" + i)).ToArray();
        foreach (var source in sources)
        {
            await edge.Subscribed(session, "acc-brain/csharp/a", RuntimeBrain.Source(source), Kinds.First);
        }
        foreach (var source in sources) { Assert.Equal(1, await source.WatchCount()); }
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APublicSourceDeliversToASubscribedRuntime(bool publishedFromAnotherBrain)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var subscriber = brain.Owner.BrainId;
        var edge = RuntimeBrain.Edge(brain, subscriber);
        var runtime = RuntimeBrain.Runtime(brain, subscriber);
        await RuntimeBrain.Programs(brain, subscriber).Assign(subscriber + "/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var source = brain.Get<IPinger>(brain.Owner.BrainId + "/public-pinger");
        await edge.Subscribed(session, subscriber + "/csharp/a", RuntimeBrain.Source(source), Pings.Pinged);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        var publisher = publishedFromAnotherBrain ? await brain.PersonAsync("publisher", ct) : brain.Owner;

        using (publisher.AsCaller()) { await source.Ping(7); }

        await changes.NextAsync(ct: ct);
        Assert.Single(await edge.PendingBatches(session));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RetryingAFailedFilterExpansionInstallsItBeforeReportingReadiness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var edge = RuntimeBrain.Edge(brain, "acc-brain");
        var runtime = RuntimeBrain.Runtime(brain, "acc-brain");
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var source = brain.Get<IExpansionSource>("acc-brain/expansion");
        var key = RuntimeBrain.Source(source);
        await edge.Subscribed(session, "acc-brain/csharp/a", key, Kinds.First);
        await source.FailNextWatch();
        await Assert.ThrowsAsync<InvalidOperationException>(() => edge.Subscribed(session, "acc-brain/csharp/a", key, Kinds.Second));
        await edge.Subscribed(session, "acc-brain/csharp/a", key, Kinds.Second);
        Assert.Contains(Kinds.Second, await source.Selection());
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        await source.EmitSecond();
        await changes.NextAsync(ct: ct);
        Assert.Single(await edge.PendingBatches(session));
    }
}

[DigitalBrain.Signal(Kinds.First)] public sealed record FirstKind : DigitalBrain.Signal;
[DigitalBrain.Signal(Kinds.Second)] public sealed record SecondKind : DigitalBrain.Signal;

public static class Kinds
{
    public const string First = "test.first-kind";
    public const string Second = "test.second-kind";
}

// A plain source (06-08): its admission of a subscriber's synapse is what a watch was, and it can fail once.
[DigitalBrain.Contracts.NeuronAlias("test.filter-expansion"), DigitalBrain.Contracts.DefaultNeuronType("test-expansion-source")]
[DigitalBrain.Access.Public]
public interface IExpansionSource
{
    Task FailNextWatch();
    Task<string[]> Selection();
    Task EmitSecond();
    Task<int> WatchCount();
}

[DigitalBrain.Neuron("test-expansion-source")]
public sealed class ExpansionSource : DigitalBrain.Neuron, IExpansionSource
{
    private bool _fail;
    private string[] _selection = [];
    private int _watches;
    public Task<int> WatchCount() => Task.FromResult(_watches);
    public Task FailNextWatch() { _fail = true; return Task.CompletedTask; }
    public Task<string[]> Selection() => Task.FromResult(_selection);
    public Task EmitSecond() => Publish(new SecondKind());
    protected override Task AdmitSynapse(DigitalBrain.SynapseRequest request)
    {
        _watches++;
        if (_fail) { _fail = false; throw new InvalidOperationException("Filter registration failed"); }
        _selection = [.. request.Signals.Select(DigitalBrain.Runtime.Orleans.Signalling.SignalLog.Alias)];
        return base.AdmitSynapse(request);
    }
}
