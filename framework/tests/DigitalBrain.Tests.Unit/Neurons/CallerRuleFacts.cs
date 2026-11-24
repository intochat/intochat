using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Neurons;

public sealed class CallerRuleProbe : Neuron
{
    public Caller Caller() => RequireCaller();

    public Caller SameBrain() => RequireSameBrain();

    public Caller SameBrainOr(string message) => RequireSameBrain(() => new UnauthorizedAccessException(message));

    public bool IsSameBrain() => IsSameBrainCaller();
}

// One caller rule on the neuron base: no caller means refused, never "treated as the owner".
public sealed class CallerRuleFacts
{
    private static readonly BrainId Brain = new("alice");
    private static readonly BrainId Other = new("bob");

    private static CallerRuleProbe Probe(Caller? caller)
    {
        var context = new FakeNeuronContext(new NeuronId(Brain, "probe", "k")) { Caller = caller };
        var probe = new CallerRuleProbe();
        NeuronAttachment.Attach(probe, context);
        return probe;
    }

    private static Caller PersonOf(BrainId brain) => new(new Actor.Person("p", "acct"), brain);

    [Fact]
    public void A_missing_caller_is_refused_by_every_form_of_the_rule()
    {
        var probe = Probe(null);

        Assert.Throws<UnauthorizedAccessException>(() => probe.Caller());
        Assert.Throws<UnauthorizedAccessException>(() => probe.SameBrain());
        Assert.Equal("mine only", Assert.Throws<UnauthorizedAccessException>(() => probe.SameBrainOr("mine only")).Message);
        Assert.False(probe.IsSameBrain());
    }

    [Fact]
    public void A_caller_of_this_brain_passes()
    {
        var caller = PersonOf(Brain);
        var probe = Probe(caller);

        Assert.Same(caller, probe.Caller());
        Assert.Same(caller, probe.SameBrain());
        Assert.Same(caller, probe.SameBrainOr("mine only"));
        Assert.True(probe.IsSameBrain());
    }

    [Fact]
    public void A_caller_of_another_brain_is_a_caller_but_not_of_this_brain()
    {
        var caller = PersonOf(Other);
        var probe = Probe(caller);

        Assert.Same(caller, probe.Caller());
        Assert.Throws<UnauthorizedAccessException>(() => probe.SameBrain());
        Assert.Equal("mine only", Assert.Throws<UnauthorizedAccessException>(() => probe.SameBrainOr("mine only")).Message);
        Assert.False(probe.IsSameBrain());
    }

    // The kernel of the system brain acts in a person's brain but is not of it; the rule does not widen for it.
    [Fact]
    public void The_kernel_of_another_brain_is_not_of_this_brain()
    {
        var probe = Probe(Caller.OfKernel(BrainId.Kernel));

        Assert.Throws<UnauthorizedAccessException>(() => probe.SameBrain());
        Assert.False(probe.IsSameBrain());
    }
}
