using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Agents;

// One request to the agent. RunId is caller-chosen for replay (at most 200 characters) or null
// for a fresh id. Model, Instructions and Contracts and Contexts override the definition for this turn only.
// Intent names the metering intent; null meters under the run id.
[GenerateSerializer, Alias("ai.agent-turn")]
public sealed record AgentTurn(
    [property: Id(0)] AiMessage Message,
    [property: Id(1)] string? RunId = null,
    [property: Id(2)] ModelSelection? Model = null,
    [property: Id(3)] string? Instructions = null,
    [property: Id(4)] string[]? Contracts = null,
    [property: Id(5)] string? Intent = null,
    [property: Id(6)] string[]? Contexts = null)
{
    public static AgentTurn Text(string message, string? runId = null, string? model = null, string? instructions = null, string[]? contracts = null, string[]? contexts = null)
        => new(new AiMessage("user", [new AiText(message)]), runId, model is { Length: > 0 } ? new(model) : null, instructions, contracts, Contexts: contexts);
}
