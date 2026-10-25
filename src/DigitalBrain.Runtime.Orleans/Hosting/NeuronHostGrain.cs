using DigitalBrain.Runtime.Orleans.Placement;
using Orleans;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>The default host: one turn at a time, placed with the brain; the neutral attributes decide what interleaves.</summary>
[GrainType("neuron.host"), BrainAffinePlacement, MayInterleave(nameof(Interleaves))]
internal sealed class NeuronHostGrain(HostedNeuronTypes types, IGrainFactory grains) : NeuronHostGrainBase(types, grains)
{
    protected override string GrainTypeName => HostedNeuronTypes.AffineGrainType;

    /// <summary>The <c>[MayInterleave]</c> predicate: reads the <see cref="DigitalBrain.Invocation.RuntimeInvocation"/> argument.</summary>
    public static bool Interleaves(IInvokable request) => NeutralCallMapping.MayInterleave(request);
}
