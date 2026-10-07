using DigitalBrain;

namespace DigitalBrain.AI.Agents;

// The model listing the Assistant still reads; turns run on IAgent.
[Alias("ai.agent-execution"), Orleans.Metadata.DefaultGrainType("ai.agent-execution")]
public interface IAgentExecution : INeuron
{
    Task<ModelChoices> Models(string? defaultModel = null);
    Task<ModelSelection?> SelectModel(string? id, string? defaultModel = null);
}
