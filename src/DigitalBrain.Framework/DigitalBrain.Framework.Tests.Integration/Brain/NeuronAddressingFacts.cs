using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Framework.Tests.Integration.Brain;

// Clean-core PR 2a: Neuron<T>(relative) is an adapter over today's raw-key addressing, so every neuron
// contract in the silo's type manifest must resolve the identical grain id through it, through Get with the
// composed key, and through the edge's untyped GetGrain(contract, key).
public sealed class NeuronAddressingFacts(FrameworkHost host)
{
    private static readonly MethodInfo NeuronMethod = typeof(IDigitalBrain).GetMethod(nameof(IDigitalBrain.Neuron))!;
    private static readonly MethodInfo GetMethod = typeof(IDigitalBrain).GetMethod(nameof(IDigitalBrain.Get))!;

    [Fact]
    public async Task EveryManifestNeuronResolvesTheSameGrainRelativelyAndByRawKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = host.Brain.Services;
        var brain = services.GetRequiredService<IDigitalBrain>();
        var grains = services.GetRequiredService<IGrainFactory>();
        var neurons = services.GetRequiredService<IOptions<GrainTypeOptions>>().Value.Classes
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(INeuron).IsAssignableFrom(type))
            .ToArray();
        // A contract several classes share (a capability such as IRetainsSignals) names no single grain; it must
        // still fail identically on every path. A contract one class owns must resolve.
        var implementers = neurons
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract != typeof(INeuron) && typeof(INeuron).IsAssignableFrom(contract) && !contract.ContainsGenericParameters)
            .GroupBy(contract => contract)
            .ToDictionary(group => group.Key, group => group.Count());
        var contracts = implementers.Keys.ToArray();
        Assert.NotEmpty(neurons);
        Assert.NotEmpty(contracts);
        Assert.Contains(typeof(ITestEmitter), contracts);
        var alice = await host.PersonAsync("alice", ct);
        var key = $"{alice.BrainId}/addressing/x";

        using var caller = alice.AsCaller();
        foreach (var contract in contracts)
        {
            var relative = Resolve(() => NeuronMethod.MakeGenericMethod(contract).Invoke(brain, [new NeuronId("addressing/x")]));
            var raw = Resolve(() => GetMethod.MakeGenericMethod(contract).Invoke(brain, [new NeuronId(key)]));
            var edge = Resolve(() => grains.GetGrain(contract, key));
            Assert.True(relative == raw && raw == edge, $"{contract.FullName}: relative {relative}, raw {raw}, edge {edge}");
            if (implementers[contract] == 1)
            { Assert.False(relative.EndsWith(nameof(Exception), StringComparison.Ordinal), $"{contract.FullName}: {relative}"); }
        }
    }

    [Fact]
    public async Task ARelativeIdKeepsTheRawKeyByteForByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain.Services.GetRequiredService<IDigitalBrain>();
        var alice = await host.PersonAsync("alice", ct);

        using var caller = alice.AsCaller();
        var id = brain.Neuron<ITestEmitter>("addressing/x").GetGrainId();

        Assert.Equal($"{alice.BrainId}/addressing/x", id.Key.ToString());
        Assert.Equal(brain.Get<ITestEmitter>($"{alice.BrainId}/addressing/x").GetGrainId().ToString(), id.ToString());
    }

    [Fact]
    public void ARelativeIdNeedsTheCallersBrain()
    {
        var brain = host.Brain.Services.GetRequiredService<IDigitalBrain>();

        Assert.Throws<InvalidOperationException>(() => brain.Neuron<ITestEmitter>("addressing/x"));
    }

    private static string Resolve(Func<object?> resolve)
    {
        try { return ((IAddressable)resolve()!).GetGrainId().ToString(); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { return error.InnerException.GetType().Name; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return error.GetType().Name; }
    }
}
