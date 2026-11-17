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
}

// Records the caller its activation ran under, and the caller of the call that woke it.
[Neuron("test.activation-caller")]
public sealed class ActivationCallerProbeNeuron : Neuron, IActivationCallerProbe
{
    private string _activatedAs = "never";

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        _activatedAs = Format(Context.Caller);
        return Task.CompletedTask;
    }

    public Task<string> Seen() => Task.FromResult($"{_activatedAs}|{Format(Context.Caller)}");

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
}
