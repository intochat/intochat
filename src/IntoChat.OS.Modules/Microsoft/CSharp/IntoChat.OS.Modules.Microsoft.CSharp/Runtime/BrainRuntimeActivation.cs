using IntoChat.OS.Framework;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// A runtime resumes its session and watches when it wakes.
internal sealed class BrainRuntimeActivation : IActivationHandler<BrainRuntimeNeuron>
{
    public Task Activated(BrainRuntimeNeuron neuron, CancellationToken cancellationToken)
        => neuron.ResumeAsync();
}
