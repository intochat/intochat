using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The agent's turn loop over a hand-built host, for facts about a transport
// rather than about the agent neuron that persists the turn.
internal static class TurnLoops
{
    public static async Task<List<AgentTurnEvent>> Collect(IServiceProvider services, string message, string[] contracts, CancellationToken ct, string[]? contexts = null, AiMessage[]? transcript = null, string conversation = "scope/agents/loop")
    {
        var plan = new TurnPlan("scope", conversation, "run", transcript ?? [], null, new AiMessage("user", [new AiText(message)]), "", contracts, contexts ?? [], null, null, 16, false);
        var events = new List<AgentTurnEvent>();
        await foreach (var item in new AgentTurnLoop(services).Run(plan, ct)) { events.Add(item); }
        return events;
    }
}
