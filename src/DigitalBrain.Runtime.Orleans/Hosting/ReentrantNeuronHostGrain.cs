using DigitalBrain.Runtime.Orleans.Placement;
using Orleans;
using Orleans.Concurrency;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>The host of a [Reentrant] neuron class: every call interleaves; placed with the brain.</summary>
[GrainType("neuron.host.reentrant"), BrainAffinePlacement, Reentrant]
internal sealed class ReentrantNeuronHostGrain(HostedNeuronTypes types, IGrainFactory grains) : NeuronHostGrainBase(types, grains)
{
    protected override string GrainTypeName => HostedNeuronTypes.ReentrantGrainType;
}
