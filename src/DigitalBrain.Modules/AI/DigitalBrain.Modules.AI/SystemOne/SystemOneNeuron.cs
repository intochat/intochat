using DigitalBrain.AI.SystemOne;
using DigitalBrain.Framework;

namespace DigitalBrain.AI;

[GrainType("ai.systemone")]
internal sealed class SystemOneNeuron(HttpSystemOne client) : Neuron, ISystemOne
{
    public Task<SystemOneResult> Ask(SystemOneRequest request, CancellationToken ct = default) => client.Ask(request, ct);
}
