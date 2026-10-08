using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class FilteredSubscriptionFacts
{
    [Fact]
    public async Task RetryingAFailedFilterExpansionInstallsItBeforeReportingReadiness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var edge = brain.Grains.GetGrain<IBrainRuntimeEdge>("acc-brain/runtime");
        var runtime = brain.Get<IBrainRuntime>("acc-brain/runtime");
        await edge.Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var source = brain.Get<IExpansionSource>("expansion");
        var key = source.GetGrainId().ToString();
        await edge.Subscribed(session, "acc-brain/csharp/a", key, nameof(FirstKind));
        await source.FailNextWatch();
        await Assert.ThrowsAsync<InvalidOperationException>(() => edge.Subscribed(session, "acc-brain/csharp/a", key, nameof(SecondKind)));
        await edge.Subscribed(session, "acc-brain/csharp/a", key, nameof(SecondKind));
        Assert.Contains(nameof(SecondKind), await source.Selection());
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        await source.EmitSecond();
        await changes.NextAsync(ct: ct);
        Assert.Single(await edge.PendingBatches(session));
    }
}

[GenerateSerializer] public sealed record FirstKind : Signal;
[GenerateSerializer] public sealed record SecondKind : Signal;
[Alias("test.filter-expansion")]
public interface IExpansionSource : INeuron
{
    Task FailNextWatch();
    Task<string[]> Selection();
    Task EmitSecond();
}

[GrainType("test-expansion-source")]
public sealed class ExpansionSource : Neuron, IExpansionSource
{
    private bool _fail;
    private string[] _selection = [];
    public Task FailNextWatch() { _fail = true; return Task.CompletedTask; }
    public Task<string[]> Selection() => Task.FromResult(_selection);
    public Task EmitSecond() => PublishAsync(new SecondKind());
    public override Task<Guid> Watch(INeuronObserver observer, string[] signals)
    {
        if (_fail) { _fail = false; throw new InvalidOperationException("Filter registration failed"); }
        _selection = signals;
        return base.Watch(observer, signals);
    }
}
