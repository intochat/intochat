using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

namespace DigitalBrain.Testing.Tests.Unit.Hosting;

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

// Owner decision 3 (17 Nov): an activation resumes stored work, so it runs as the scheduler of its brain whoever's call woke it,
// as the runtime host does.
public sealed class ActivationCallerFacts
{
    private static readonly BrainId Brain = new("testos-activation");

    [Fact]
    public async Task ActivationRunsAsTheSchedulerEvenWhenAPersonWakesTheNeuron()
    {
        await using var os = global::DigitalBrain.Testing.TestOS.Local().Register<ActivationCallerProbeNeuron>();
        var id = new NeuronId(Brain, "test.activation-caller", "default");

        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            Assert.Equal($"Scheduler:{Brain.Value}|Person:{Brain.Value}", await os.Get<IActivationCallerProbe>(id).Seen());
        }

        await os.Deactivate(id);
        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            Assert.Equal($"Scheduler:{Brain.Value}|Person:{Brain.Value}", await os.Get<IActivationCallerProbe>(id).Seen());
        }
    }
}
