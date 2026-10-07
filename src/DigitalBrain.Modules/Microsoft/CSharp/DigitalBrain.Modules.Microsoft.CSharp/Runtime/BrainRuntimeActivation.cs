using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// A runtime that was watching when it deactivated watches again when it wakes.
internal sealed class BrainRuntimeActivation : IActivationHandler<BrainRuntimeNeuron>
{
    public Task Activated(BrainRuntimeNeuron neuron, CancellationToken cancellationToken)
    {
        neuron.ResumeWatches();
        return Task.CompletedTask;
    }
}
