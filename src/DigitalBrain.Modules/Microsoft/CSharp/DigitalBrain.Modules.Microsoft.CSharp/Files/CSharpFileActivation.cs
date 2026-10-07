using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Files;

// A file that was watching when it deactivated watches again when it wakes.
internal sealed class CSharpFileActivation : IActivationHandler<CSharpFileNeuron>
{
    public Task Activated(CSharpFileNeuron neuron, CancellationToken cancellationToken)
    {
        neuron.ResumeWatches();
        return Task.CompletedTask;
    }
}
