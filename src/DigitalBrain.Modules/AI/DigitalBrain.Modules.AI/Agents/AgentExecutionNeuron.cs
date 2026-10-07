using DigitalBrain.Framework;

namespace DigitalBrain.AI.Agents;

// The model listing the Assistant still reads through this grain; turns run on IAgent.
[GrainType("ai.agent-execution")]
internal sealed class AgentExecutionNeuron(ModelCatalog catalog) : Neuron, IAgentExecution
{
    public override NeuronAccess Access(string operation) => new(BrainScope.CurrentId());

    public Task<ModelChoices> Models(string? defaultModel = null) => catalog.List(defaultModel, CancellationToken.None);
    public Task<ModelSelection?> SelectModel(string? id, string? defaultModel = null) => catalog.Select(id, defaultModel, CancellationToken.None);
}
