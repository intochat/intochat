using DigitalBrain.Framework;

namespace DigitalBrain.AI.Agents;

// An agent loads its storage on wake and marks a run the previous activation left open as interrupted.
internal sealed class AgentActivation : IActivationHandler<AgentNeuron>
{
    public Task Activated(AgentNeuron neuron, CancellationToken cancellationToken) => neuron.ResumeAsync();
}
