using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;

[DefaultNeuronType("test.activation-caller")]
public interface IActivationCallerProbe
{
    Task<string> Seen();

    Task DeferFromCall();

    Task<string> Deferred();
}

// Records the caller its activation ran under, and the caller of the call that woke it.
[Neuron("test.activation-caller")]
public sealed class ActivationCallerProbeNeuron : Neuron, IActivationCallerProbe
{
    private string _activatedAs = "never";
    private string _deferredOnActivate = "never";
    private string _deferredFromCall = "never";

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        _activatedAs = Format(Context.Caller);
        Defer(() => { _deferredOnActivate = Format(Context.Caller); return Task.CompletedTask; });
        return Task.CompletedTask;
    }

    public Task<string> Seen() => Task.FromResult($"{_activatedAs}|{Format(Context.Caller)}");

    public Task DeferFromCall()
    {
        Defer(() => { _deferredFromCall = Format(Context.Caller); return Task.CompletedTask; });
        return Task.CompletedTask;
    }

    public Task<string> Deferred() => Task.FromResult($"{_deferredOnActivate}|{_deferredFromCall}");

    private static string Format(Caller? caller) => caller is null ? "null" : $"{caller.Actor.GetType().Name}:{caller.Brain.Value}";
}

// Owner decision 3 (17 Nov): an activation resumes stored work, so it runs as the scheduler of its brain whoever's call woke it.
public sealed class ActivationCallerFacts(EdgeAuthorityIntegrationFacts.Fixture fixture)
    : IClassFixture<EdgeAuthorityIntegrationFacts.Fixture>
{
    [Fact]
    public async Task ActivationRunsAsTheSchedulerEvenWhenAPersonWakesTheNeuron()
    {
        var edge = fixture.Services.GetRequiredService<EdgeAuthority>();
        var brain = fixture.NewBrain("activation");
        var probe = SiloNeurons.Get<IActivationCallerProbe>(fixture.Services, new NeuronId(brain, "test.activation-caller", "default"));

        using (edge.Enter(edge.Person("alice", "acct-1", brain)))
        {
            Assert.Equal($"Scheduler:{brain.Value}|Person:{brain.Value}", await probe.Seen());
        }
    }

    // Work the activation defers (as AppNeuron defers its restore) runs as the scheduler; work a person's call defers runs as them.
    [Fact]
    public async Task DeferredWorkRunsAsTheCallerThatDeferredIt()
    {
        var edge = fixture.Services.GetRequiredService<EdgeAuthority>();
        var brain = fixture.NewBrain("activation-defer");
        var probe = SiloNeurons.Get<IActivationCallerProbe>(fixture.Services, new NeuronId(brain, "test.activation-caller", "default"));

        using (edge.Enter(edge.Person("alice", "acct-1", brain)))
        {
            await probe.DeferFromCall();
            Assert.Equal($"Scheduler:{brain.Value}|Person:{brain.Value}", await probe.Deferred());
        }
    }
}
