using DigitalBrain.Neurons;
using DigitalBrain.Kernel;
using System.Collections.Concurrent;
using OS = DigitalBrain.Testing.TestOS;

namespace DigitalBrain.Testing.Tests.Unit.Hosting;

public interface IClaimingSink
{
    Task SubscribeClaiming(NeuronId source, string principal);
}

[Neuron("rx.claiming")]
public sealed class ClaimingSink : Neuron, IClaimingSink
{
    public Task SubscribeClaiming(NeuronId source, string principal)
    {
        _ = principal; // named in the body; nothing reads it
        return Context.Wires.Ensure<Number>(source);
    }
}

[Neuron("rx.scoped")]
public sealed class ScopedSource : Neuron<Tally>, INumberSource
{
    public static readonly ConcurrentDictionary<string, string?> Seen = new();

    public Task Emit(int value) => Save(State with { Total = value }, new Number(value));

    public Task EmitOther() => Save(State, new Other());

    protected override async Task AdmitSynapse(SynapseRequest request)
    {
        var principal = Context.Caller?.For?.Principal;
        Seen[request.Subscriber.ToString()] = principal;
        await base.AdmitSynapse(request);
        if (principal != "alice") { throw new UnauthorizedAccessException("not alice"); }
    }
}

// 04-19: admission decides on the stamped caller of the subscribing call; a caller named in the subscriber's call body is never read.
public sealed class AdmissionCallerFacts
{
    private static readonly NeuronId Source = NeuronId.Parse("adm/rx.scoped/s");
    private static readonly BrainId Brain = new("adm");

    [Fact]
    public async Task Admission_sees_the_stamped_caller_and_ignores_one_in_the_body()
    {
        await using var os = OS.Local().Register<ScopedSource>().Register<ClaimingSink>();
        var forged = NeuronId.Parse("adm/rx.claiming/forged");
        using (os.Edge.Enter(os.Edge.Person("mallory", "acct-2", Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<IClaimingSink>(forged).SubscribeClaiming(Source, "alice"));
        }
        Assert.Equal("mallory", ScopedSource.Seen[forged.ToString()]);
        Assert.Empty(os.SynapsesOf(forged));

        var stamped = NeuronId.Parse("adm/rx.claiming/stamped");
        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            await os.Get<IClaimingSink>(stamped).SubscribeClaiming(Source, "mallory");
        }
        Assert.Equal("alice", ScopedSource.Seen[stamped.ToString()]);
        Assert.Single(os.SynapsesOf(stamped));
    }

    [Fact]
    public async Task An_unstamped_subscribing_call_is_refused()
    {
        await using var os = OS.Local().Register<ScopedSource>().Register<ClaimingSink>();
        var unstamped = NeuronId.Parse("adm/rx.claiming/unstamped");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<IClaimingSink>(unstamped).SubscribeClaiming(Source, "alice"));
        Assert.Empty(os.SynapsesOf(unstamped));
    }

    [Fact]
    public async Task The_legacy_admission_overload_still_decides_when_only_it_is_overridden()
    {
        await using var os = OS.Local().Register<ClosedSource>().Register<ClaimingSink>();
        using (os.Edge.Enter(os.Edge.Person("alice", "acct-1", Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<IClaimingSink>(NeuronId.Parse("adm/rx.claiming/k")).SubscribeClaiming(NeuronId.Parse("adm/rx.closed/s"), "alice"));
        }
    }
}