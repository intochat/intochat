using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

namespace DigitalBrain.Testing.Tests.Unit.Hosting;

[Signal("test.reaction-caller.rang")]
public sealed record Rang : Signal;

[DefaultNeuronType("test.reaction-caller.bell")]
public interface IReactionBell
{
    Task Ring();
}

[Neuron("test.reaction-caller.bell")]
public sealed class ReactionBellNeuron : Neuron, IReactionBell
{
    public Task Ring() => Publish(new Rang());
}

[DefaultNeuronType("test.reaction-caller.ear")]
public interface IReactionEar
{
    Task Follow(NeuronId bell);

    Task<string[]> Heard();
}

public sealed record ReactionEarState(string[] Heard)
{
    public ReactionEarState() : this([]) { }
}

// Records who each reaction ran as.
[Neuron("test.reaction-caller.ear")]
public sealed class ReactionEarNeuron : Neuron<ReactionEarState>, IReactionEar
{
    public Task Follow(NeuronId bell) => Context.Wires.Ensure<Rang>(bell);

    public Task<string[]> Heard() => Task.FromResult(State.Heard);

    [On]
    public Task On(Rang rang)
        => Store(new ReactionEarState([.. State.Heard, Context.Caller is { } caller ? $"{caller.Actor.GetType().Name}:{caller.Brain.Value}" : "null"]));
}

// A reaction is the brain's own work and always runs with a caller: the scheduler of the brain when nothing carried one,
// as on the silo (ReactionCallerFacts in the Orleans integration tier).
public sealed class ReactionCallerFacts
{
    private static readonly BrainId Brain = new("testos-reaction-caller");
    private static readonly NeuronId Bell = new(Brain, "test.reaction-caller.bell", "hall");
    private static readonly NeuronId Ear = new(Brain, "test.reaction-caller.ear", "hall");

    [Fact]
    public async Task A_catch_up_on_an_activation_no_caller_woke_reacts_as_the_brains_scheduler()
    {
        await using var os = global::DigitalBrain.Testing.TestOS.Local().Register<ReactionBellNeuron>().Register<ReactionEarNeuron>();
        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            await os.Get<IReactionEar>(Ear).Follow(Bell);
            await os.Deactivate(Ear);
            await os.Get<IReactionBell>(Bell).Ring();
        }

        await os.Activate(Ear);
        await os.Idle();

        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            Assert.Equal([$"{nameof(Actor.Scheduler)}:{Brain.Value}"], await os.Get<IReactionEar>(Ear).Heard());
        }
    }

    [Fact]
    public async Task A_reaction_to_the_brains_own_person_keeps_that_person()
    {
        await using var os = global::DigitalBrain.Testing.TestOS.Local().Register<ReactionBellNeuron>().Register<ReactionEarNeuron>();
        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            await os.Get<IReactionEar>(Ear).Follow(Bell);
            await os.Get<IReactionBell>(Bell).Ring();
            await os.Idle();
            Assert.Equal([$"{nameof(Actor.Person)}:{Brain.Value}"], await os.Get<IReactionEar>(Ear).Heard());
        }
    }
}
