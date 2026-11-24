using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Synapses;

/// <summary>A subscriber that records who each reaction ran as: "{actor}@{brain}", or "none".</summary>
[Neuron("probe.caller.listener")]
public sealed class CallerListenerNeuron : Neuron, IListener
{
    public Task Follow(string source) => Context.Wires.Ensure<Counted>(NeuronId.Parse(source));

    public Task FollowNotes(string source) => Context.Wires.Ensure<Noted>(NeuronId.Parse(source));

    public Task Unfollow(string source) => Context.Wires.Remove(NeuronId.Parse(source));

    public Task<int> Ping() => Task.FromResult(1);

    [On]
    public Task On(Counted signal)
    {
        Heard.Add(Id.ToString(), Context.Caller is { } caller ? $"{caller.Actor.GetType().Name}@{caller.Brain.Value}" : "none");
        return Task.CompletedTask;
    }
}

// A reaction is the brain's own work: it always runs with a caller, the scheduler of the brain when nothing else carried
// one. A catch-up on activation runs under whatever message woke the grain; a reminder tick carries no caller at all.
public sealed class ReactionCallerFacts(SynapseSilo silo) : IClassFixture<SynapseSilo>
{
    [Fact]
    public async Task A_catch_up_woken_by_a_callerless_reminder_tick_reacts_as_the_brains_scheduler()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Listener = "b1/probe.caller.listener/wake", Source = "b1/probe.counter/wake";
        using (TestStamp.Kernel())
        {
            await silo.Get<IListener>(Listener).Follow(Source);
            silo.Faults.FailAfterAppend = true;
            try { await Assert.ThrowsAnyAsync<Exception>(() => silo.Get<ICounter>(Source).Add(1)); }
            finally { silo.Faults.FailAfterAppend = false; }
        }
        Assert.Empty(Heard.Of(Listener));
        await silo.DeactivateAll().WaitAsync(ct);

        // The Orleans reminder service delivers a tick with an empty request context; it wakes the listener, which catches up.
        var grains = silo.Services.GetRequiredService<IGrainFactory>();
        await grains.GetGrain<IRemindable>(GrainId.Create("neuron.host", Listener)).ReceiveReminder("probe.wake", default, ct);

        await Heard.Until(Listener, 1, ct);
        Assert.Equal(["Scheduler@b1"], Heard.Of(Listener));
    }
}
