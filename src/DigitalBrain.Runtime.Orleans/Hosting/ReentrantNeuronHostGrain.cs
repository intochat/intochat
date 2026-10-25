using DigitalBrain.Runtime.Orleans.Placement;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>The host of a [Reentrant] neuron class: every call interleaves; placed with the brain.</summary>
[GrainType("neuron.host.reentrant"), BrainAffinePlacement, global::Orleans.Concurrency.Reentrant]
internal sealed class ReentrantNeuronHostGrain(HostedNeuronTypes types, IGrainFactory grains) : NeuronHostGrainBase(types, grains)
{
    protected override string GrainTypeName => HostedNeuronTypes.ReentrantGrainType;
}
