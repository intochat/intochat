using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using System.Collections.Concurrent;
using DigitalBrain.Access;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Synapses;

public interface IForger
{
    /// <summary>Asks for a synapse while naming a caller in its own arguments, as a forging module would.</summary>
    Task FollowClaiming(string source, string principal, string brain);
}

/// <summary>A subscriber that puts a caller in the call body; the runtime must not read it.</summary>
[Neuron("probe.forger")]
public sealed class ForgerNeuron : Neuron, IForger
{
    public static readonly ConcurrentDictionary<string, string> Claimed = new();

    public Task FollowClaiming(string source, string principal, string brain)
    {
        Claimed[Id.ToString()] = $"{principal}@{brain}";
        return Context.Wires.Ensure<Counted>(NeuronId.Parse(source));
    }
}

public interface IScoped
{
    Task<int> Ping();
}

/// <summary>A source that admits only a call stamped with principal <c>alice</c> of its own brain, recording what it saw.</summary>
[Neuron("probe.scoped")]
public sealed class ScopedNeuron : Neuron, IScoped
{
    public static readonly ConcurrentDictionary<string, Caller?> Seen = new();

    public Task<int> Ping() => Task.FromResult(1);

    protected override async Task AdmitSynapse(SynapseRequest request)
    {
        var caller = Context.Caller;
        Seen[request.Subscriber.ToString()] = caller;
        await base.AdmitSynapse(request);
        if (caller?.For is not { Principal: "alice" } || caller.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException($"{request.Subscriber} is not admitted for {caller?.For?.Principal ?? "no caller"}.");
        }
    }
}

// 04-19, R4-19: AdmitSynapse sees the caller the kernel stamped (Context.Caller), never a caller the subscriber sends.
public sealed class AdmissionCallerFacts(SynapseSilo silo) : IClassFixture<SynapseSilo>
{
    private const string Source = "ac/probe.scoped/s";
    private static readonly BrainId Brain = new("ac");

    private EdgeAuthority Edge => silo.Services.GetRequiredService<EdgeAuthority>();

    [Fact]
    public async Task Admission_receives_the_stamped_caller_and_admits_it()
    {
        var subscriber = "ac/probe.forger/stamped";
        using (Edge.Enter(Edge.App("mod.tables", new Actor.Person("alice", "acct-1"), Brain)))
        {
            await silo.Get<IForger>(subscriber).FollowClaiming(Source, "alice", "ac");
        }
        var seen = ScopedNeuron.Seen[subscriber];
        Assert.Equal(new Actor.App("mod.tables", new Actor.Person("alice", "acct-1")), seen?.Actor);
    }

    [Fact]
    public async Task A_caller_forged_in_the_call_body_is_ignored_and_an_unstamped_call_is_refused()
    {
        var subscriber = "ac/probe.forger/forged";
        // Unstamped, the subscribing call itself is refused before the forger runs.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => silo.Get<IForger>(subscriber).FollowClaiming(Source, "alice", "ac"));
        Assert.False(ForgerNeuron.Claimed.ContainsKey(subscriber));
        Assert.False(ScopedNeuron.Seen.ContainsKey(subscriber));
    }

    [Fact]
    public async Task A_stamped_caller_wins_over_a_different_caller_in_the_body()
    {
        var subscriber = "ac/probe.forger/mallory";
        using (Edge.Enter(Edge.Person("mallory", "acct-2", Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => silo.Get<IForger>(subscriber).FollowClaiming(Source, "alice", "ac"));
        }
        Assert.Equal("mallory", ScopedNeuron.Seen[subscriber]?.For?.Principal);
        Assert.Equal("alice@ac", ForgerNeuron.Claimed[subscriber]);
    }
}
