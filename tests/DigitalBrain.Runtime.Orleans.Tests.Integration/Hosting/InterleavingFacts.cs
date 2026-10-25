using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;

public sealed class InterleavingFacts(HostedSilo silo) : IClassFixture<HostedSilo>
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(700);

    /// <summary>Holds a turn on <paramref name="id"/>, runs <paramref name="other"/> and reports whether it finished while the turn was held.</summary>
    private async Task<bool> RunsDuringHold(string id, Func<ITurnProbe, Task> other)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = silo.Get<ITurnProbe>(id);
        var gate = id + "#" + Guid.NewGuid().ToString("N");
        var hold = probe.Hold(gate);
        await ProbeLog.Until(() => TurnLog.Held.ContainsKey(gate), ct);
        var call = other(probe);
        var during = await Task.WhenAny(call, Task.Delay(Window, ct)) == call;
        ProbeLog.Gate(gate).SetResult();
        await hold;
        await call;
        return during;
    }

    [Fact]
    public async Task Interleaves_and_ReadOnlyCall_run_during_a_suspended_turn_and_plain_methods_do_not()
    {
        Assert.True(await RunsDuringHold("b1/turn/attrs", probe => probe.Peek()));
        Assert.True(await RunsDuringHold("b1/turn/attrs", probe => probe.Read()));
        Assert.False(await RunsDuringHold("b1/turn/attrs", probe => probe.Plain()));
    }

    [Fact]
    public async Task InterleavesWhen_interleaves_exactly_when_the_static_predicate_over_the_RuntimeInvocation_says_so()
    {
        Assert.True(await RunsDuringHold("b1/turn/when", probe => probe.Flagged(true)));
        Assert.False(await RunsDuringHold("b1/turn/when", probe => probe.Flagged(false)));
    }

    [Fact]
    public async Task An_inherited_predicate_applies_and_a_redeclared_attribute_uses_its_own()
    {
        // TurnProbe inherits BaseRule from its abstract base.
        Assert.True(await RunsDuringHold("b1/turn/inherited", probe => probe.Flagged(true)));
        Assert.False(await RunsDuringHold("b1/turn/inherited", probe => probe.Plain()));
        // OwnRuleTurnProbe redeclares: Plain interleaves, Flagged(true) no longer does.
        Assert.True(await RunsDuringHold("b1/turn.own/x", probe => probe.Plain()));
        Assert.False(await RunsDuringHold("b1/turn.own/x", probe => probe.Flagged(true)));
    }

    [Fact]
    public async Task A_Reentrant_class_is_hosted_by_neuron_host_reentrant_and_interleaves_everything()
    {
        var id = NeuronId.Parse("b1/turn.reentrant/r");
        var host = new OrleansInvocationSink(silo.Grains, silo.Services.GetRequiredService<HostedNeuronTypes>(), null)
            .Host(id, new DigitalBrain.Invocation.RuntimeInvocation("", DigitalBrain.Invocation.NeuronProxy.ContractName(typeof(ITurnProbe)), id.ToString(), "Plain", []));
        Assert.Equal("neuron.host.reentrant", host.GetGrainId().Type.ToString());
        Assert.True(await RunsDuringHold("b1/turn.reentrant/r", probe => probe.Plain()));
        Assert.True(await RunsDuringHold("b1/turn.reentrant/r", probe => probe.Flagged(false)));
    }

    [Fact]
    public async Task Post_and_Stream_are_scheduled_by_the_same_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await RunsDuringHold("b1/turn/post", async probe =>
        {
            await probe.NoteNow("now");
            await ProbeLog.Until(() => TurnLog.Noted.ContainsKey("now"), ct);
        }));
        Assert.False(await RunsDuringHold("b1/turn/post", async probe =>
        {
            await probe.Note("later");
            await ProbeLog.Until(() => TurnLog.Noted.ContainsKey("later"), ct);
        }));
        Assert.True(await RunsDuringHold("b1/turn/stream", async probe => Assert.Equal([6], await probe.ItemsNow().ToListAsync(ct))));
        Assert.False(await RunsDuringHold("b1/turn/stream", async probe => Assert.Equal([6], await probe.Items().ToListAsync(ct))));
    }
}
