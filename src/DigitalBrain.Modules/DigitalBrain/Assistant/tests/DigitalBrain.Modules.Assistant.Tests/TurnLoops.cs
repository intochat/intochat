using DigitalBrain.AI;
using DigitalBrain.AI.Agents;

namespace DigitalBrain.Modules.Assistant.Tests;

// The agent's turn loop over a hand-built host: real tool dispatch, sessions and model calls,
// without the agent neuron that persists the turn.
internal static class TurnLoops
{
    public static IAsyncEnumerable<AgentTurnEvent> Run(IServiceProvider services, string scope, AgentTurn turn, CancellationToken ct)
        => new AgentTurnLoop(services).Run(new TurnPlan(scope, turn.RunId ?? "run", [], null, turn.Message, turn.Instructions ?? "",
            turn.Tools ?? [], [], turn.Model, null, 16, Streaming: false), ct);

    public static async Task<List<AgentTurnEvent>> Collect(IServiceProvider services, string scope, string message, string[] tools, CancellationToken ct)
    {
        var events = new List<AgentTurnEvent>();
        await foreach (var item in Run(services, scope, AgentTurn.Text(message, runId: "run", tools: tools), ct)) { events.Add(item); }
        return events;
    }
}
