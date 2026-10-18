using IntoChat.OS.AI.Inference;
using IntoChat.OS.AI.Models;

namespace IntoChat.OS.AI.Agents;

// Contracts are full type names of the neuron contracts the model may act on.
// Contexts are IAgentContext neuron keys the agent reads at the start of every turn.
[GenerateSerializer, Alias("ai.agent-definition")]
public sealed record AgentDefinition
{
    [Id(0)] public string DisplayName { get; init; } = "Assistant";
    [Id(1)] public string Description { get; init; } = "";
    [Id(2)] public string Instructions { get; init; } = "";
    [Id(3)] public ModelSelection? Model { get; init; }
    [Id(4)] public string[] Contracts { get; init; } = [];
    [Id(5)] public string[] Capabilities { get; init; } = [];
    [Id(6)] public string[] RoutingExamples { get; init; } = [];
    [Id(7)] public int MaxModelCalls { get; init; } = 16;
    [Id(8)] public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    [Id(9)] public int MaxHistoryMessages { get; init; } = 256;
    [Id(10)] public InferenceOptions? Options { get; init; }
    [Id(11)] public string[] Contexts { get; init; } = [];
}
