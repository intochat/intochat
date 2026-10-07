using DigitalBrain.AI;
using DigitalBrain.AI.Agents;

namespace DigitalBrain.Modules.AI.Tests;

// The agent's turn loop over a hand-built host, for facts about a transport or a tool source
// rather than about the agent neuron that persists the turn.
internal static class TurnLoops
{
    public static async Task<List<AgentTurnEvent>> Collect(IServiceProvider services, string message, string[] tools, CancellationToken ct)
    {
        var plan = new TurnPlan("scope", "run", [], null, new AiMessage("user", [new AiText(message)]), "", tools, [], null, null, 16, Streaming: false);
        var events = new List<AgentTurnEvent>();
        await foreach (var item in new AgentTurnLoop(services).Run(plan, ct)) { events.Add(item); }
        return events;
    }
}
