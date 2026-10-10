using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class FilteredSubscriptionFacts
{
    [Theory]
    [InlineData("subscriber-brain")]
    [InlineData("publisher-brain")]
    public async Task APublicSourceDeliversToASubscribedRuntime(string publisherBrain)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var edge = brain.Grains.GetGrain<IBrainRuntimeEdge>("subscriber-brain/runtime");
        var runtime = brain.Get<IBrainRuntime>("subscriber-brain/runtime");
        await brain.Grains.GetGrain<IBrainPrograms>("subscriber-brain/runtime").Assign("subscriber-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var source = brain.Get<IPinger>("public-pinger");
        await edge.Subscribed(session, "subscriber-brain/csharp/a", source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        var caller = CallerContextStamper.Stamp(new()
        {
            PrincipalId = "publisher-owner",
            AccountId = "publisher-account",
            BrainId = publisherBrain,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            await brain.AuthorizeCallerAsync();
            await source.Ping(7);
            Assert.Equal(caller, CallerContextStamper.Require());
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
        await changes.NextAsync(ct: ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Single(await edge.PendingBatches(session));
    }

    [Fact]
    public async Task RetryingAFailedFilterExpansionInstallsItBeforeReportingReadiness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var edge = brain.Grains.GetGrain<IBrainRuntimeEdge>("acc-brain/runtime");
        var runtime = brain.Get<IBrainRuntime>("acc-brain/runtime");
        await brain.Grains.GetGrain<IBrainPrograms>("acc-brain/runtime").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
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
