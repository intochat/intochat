using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration;

// Opted in to calls from other brains: its Shared and Public operations cross, its others do not.
[Shareable, DefaultNeuronType("test.crossing")]
public interface ICrossing
{
    Task<string> OwnerOnly();
    [Shared] Task<string> SharedOp();
    [Public] Task<string> PublicOp();
    [Internal] Task<string> InternalOp();
}

// Not opted in: Shared and Public mean what they meant before, and nothing here crosses.
[DefaultNeuronType("test.crossing")]
public interface ICrossingUnmarked
{
    [Shared] Task<string> SharedUnmarked();
    [Public] Task<string> PublicUnmarked();
}

// A base contract that did not opt in, and a contract that did and inherits from it. Shared on the derived contract must
// not open the base's methods to another brain.
public interface ICrossingBase
{
    Task<string> Inherited();
}

[Shareable, Shared, DefaultNeuronType("test.crossing")]
public interface ICrossingDerived : ICrossingBase
{
    Task<string> Own();
}

// A contract that is not visible: Internal by default, and admitted without denial while internal contracts are only audited.
[DefaultNeuronType("test.crossing")]
internal interface ICrossingHidden
{
    Task<string> Hidden();
}

[Neuron("test.crossing")]
public sealed class CrossingNeuron : Neuron, ICrossing, ICrossingUnmarked, ICrossingHidden, ICrossingDerived
{
    private string Seen => $"{Id.Brain.Value}<-{Context.Caller?.Brain.Value}";

    public Task<string> OwnerOnly() => Task.FromResult(Seen);
    public Task<string> SharedOp() => Task.FromResult(Seen);
    public Task<string> PublicOp() => Task.FromResult(Seen);
    public Task<string> InternalOp() => Task.FromResult(Seen);
    public Task<string> Hidden() => Task.FromResult(Seen);
    public Task<string> Inherited() => Task.FromResult(Seen);
    public Task<string> Own() => Task.FromResult(Seen);
    public Task<string> SharedUnmarked() => Task.FromResult(Seen);
    public Task<string> PublicUnmarked() => Task.FromResult(Seen);
}

// A call between brains reaches a neuron only when the operation is Shared or Public on a [Shareable] contract. The fixture audits internal
// contracts rather than denying them (the default), so the runtime's own refusal is the wall these facts prove.
public sealed class CrossBrainAccessFacts(PlainNeuronCallFilterFacts.FilterSiloFixture fixture)
    : IClassFixture<PlainNeuronCallFilterFacts.FilterSiloFixture>
{
    private (ICrossing Open, ICrossingHidden Hidden, string Victim, string Attacker) Scene()
    {
        var victim = fixture.NewBrain("crossing-victim");
        var attacker = fixture.NewBrain("crossing-attacker");
        return (SiloNeurons.Get<ICrossing>(fixture.Services, $"{victim}/main"),
            SiloNeurons.Get<ICrossingHidden>(fixture.Services, $"{victim}/main"), victim, attacker);
    }

    public enum Kind { User, App, Agent }

    private IDisposable As(string brain, Kind kind = Kind.User)
    {
        var edge = fixture.Services.GetRequiredService<EdgeAuthority>();
        var person = new Actor.Person(brain, brain);
        return edge.Enter(kind switch
        {
            Kind.App => edge.App($"{brain}/apps/any", person, new BrainId(brain)),
            Kind.Agent => edge.Agent(person, new BrainId(brain)),
            _ => edge.Person(brain, brain, new BrainId(brain)),
        });
    }

    [Theory]
    [InlineData(Kind.User)]
    [InlineData(Kind.App)]
    [InlineData(Kind.Agent)]
    public async Task A_shared_or_public_operation_is_reached_from_another_brain_with_the_callers_own_brain_stamped(Kind kind)
    {
        var (open, _, victim, attacker) = Scene();
        using var stamp = As(attacker, kind);

        Assert.Equal($"{victim}<-{attacker}", await open.SharedOp());
        Assert.Equal($"{victim}<-{attacker}", await open.PublicOp());
    }

    [Theory]
    [InlineData(Kind.User)]
    [InlineData(Kind.App)]
    [InlineData(Kind.Agent)]
    public async Task Every_other_operation_is_refused_from_another_brain(Kind kind)
    {
        var (open, hidden, _, attacker) = Scene();
        using var stamp = As(attacker, kind);

        var owner = await Assert.ThrowsAsync<UnauthorizedAccessException>(open.OwnerOnly);
        var marked = await Assert.ThrowsAsync<UnauthorizedAccessException>(open.InternalOp);
        var unseen = await Assert.ThrowsAsync<UnauthorizedAccessException>(hidden.Hidden);
        Assert.All(new[] { owner, marked, unseen }, error => Assert.Contains("Cross-brain security violation", error.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Kind.User)]
    [InlineData(Kind.App)]
    public async Task Shared_or_public_on_a_contract_that_did_not_opt_in_is_refused_from_another_brain(Kind kind)
    {
        var (_, _, victim, attacker) = Scene();
        var unmarked = SiloNeurons.Get<ICrossingUnmarked>(fixture.Services, $"{victim}/main");
        using var stamp = As(attacker, kind);

        var shared = await Assert.ThrowsAsync<UnauthorizedAccessException>(unmarked.SharedUnmarked);
        var open = await Assert.ThrowsAsync<UnauthorizedAccessException>(unmarked.PublicUnmarked);
        Assert.All(new[] { shared, open }, error => Assert.Contains("Cross-brain security violation", error.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_method_inherited_from_a_contract_that_did_not_opt_in_is_refused_through_the_one_that_did()
    {
        var (_, _, victim, attacker) = Scene();
        var derived = SiloNeurons.Get<ICrossingDerived>(fixture.Services, $"{victim}/main");
        using var stamp = As(attacker);

        Assert.Equal($"{victim}<-{attacker}", await derived.Own());
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(derived.Inherited);
        Assert.Contains("Cross-brain security violation", error.Message, StringComparison.Ordinal);
    }

    // The wall reads the legacy stamp; the access gate reads the caller ambient. A call that carries only the legacy
    // stamp of another brain must not pass the wall and then be admitted by the gate as this brain's own kernel.
    [Fact]
    public async Task A_legacy_only_stamp_of_another_brain_does_not_cross()
    {
        var (open, _, _, attacker) = Scene();
        global::Orleans.Runtime.RequestContext.Set("db.cap.principal", attacker);
        global::Orleans.Runtime.RequestContext.Set("db.cap.account", attacker);
        global::Orleans.Runtime.RequestContext.Set("db.cap.brain", attacker);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(open.SharedOp);
        }
        finally
        {
            global::Orleans.Runtime.RequestContext.Remove("db.cap.principal");
            global::Orleans.Runtime.RequestContext.Remove("db.cap.account");
            global::Orleans.Runtime.RequestContext.Remove("db.cap.brain");
        }
    }

    [Fact]
    public async Task The_same_brain_still_reaches_its_own_operations()
    {
        var (open, _, victim, _) = Scene();
        using var stamp = As(victim);

        Assert.Equal($"{victim}<-{victim}", await open.OwnerOnly());
        Assert.Equal($"{victim}<-{victim}", await open.SharedOp());
    }

    [Fact]
    public async Task A_host_method_that_carries_no_call_is_refused_from_another_brain()
    {
        var (open, _, victim, attacker) = Scene();
        using (As(victim)) { await open.OwnerOnly(); }

        using var stamp = As(attacker);
        var host = fixture.Services.GetRequiredService<IGrainFactory>().GetGrain<INeuronHost>(GrainId.Create("neuron.host", $"{victim}/test.crossing/main"));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.InspectState(TestContext.Current.CancellationToken));
        Assert.Contains("Cross-brain security violation", error.Message, StringComparison.Ordinal);
    }

    // Three host grains implement INeuronHost: the observer names the neuron's own host grain, so the read reaches it, and
    // the wall above then decides who gets the state.
    [Fact]
    public async Task The_observer_reads_a_neurons_state_for_its_own_brain_and_refuses_another_brain()
    {
        var ct = TestContext.Current.CancellationToken;
        var victim = fixture.NewBrain("observed-victim");
        var attacker = fixture.NewBrain("observed-attacker");
        var id = SiloNeurons.IdOf(typeof(ITestUserSettings), $"{victim}/main");
        var observer = fixture.Services.GetRequiredService<DigitalBrain.Kernel.IBrainObserver>();

        using (As(victim))
        {
            await SiloNeurons.Get<ITestUserSettings>(fixture.Services, id).SetProfile("ada");
            var state = await observer.State(id, ct);
            Assert.Contains("ada", state!.Value.GetRawText(), StringComparison.Ordinal);
        }

        using (As(attacker))
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.State(id, ct));
            Assert.Contains("Cross-brain security violation", error.Message, StringComparison.Ordinal);
        }
    }
}
