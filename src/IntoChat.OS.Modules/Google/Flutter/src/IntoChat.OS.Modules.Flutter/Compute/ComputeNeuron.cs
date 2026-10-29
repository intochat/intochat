using DigitalBrain;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Flutter.Compute;

// Pricing returns in 0.8.0. The neuron still answers the shell with an empty window.
[Neuron("ui.compute")]
internal sealed class ComputeNeuron : DigitalBrain.Neuron, ICompute
{
    public Task<ComputeState> Read()
    {
        if ($"{Id.Brain.Value}/{Id.Key}" != BrainScope.CurrentId() + "/compute")
        { throw new UnauthorizedAccessException("Read usage only through the current brain's compute neuron."); }
        return Task.FromResult(new ComputeState(0, DateTimeOffset.UtcNow.AddDays(-30), []));
    }
}
