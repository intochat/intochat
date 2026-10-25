using DigitalBrain.Runtime.Orleans.Placement;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>The default host: one turn at a time, placed with the brain.</summary>
[GrainType("neuron.host"), BrainAffinePlacement]
internal sealed class NeuronHostGrain(HostedNeuronTypes types, IGrainFactory grains) : NeuronHostGrainBase(types, grains)
{
    protected override string GrainTypeName => HostedNeuronTypes.AffineGrainType;
}
