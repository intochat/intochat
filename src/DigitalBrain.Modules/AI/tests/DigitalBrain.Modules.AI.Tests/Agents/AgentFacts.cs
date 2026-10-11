using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Modules.AI.Tests.Agents;

// Agents in these facts live in the Open posture owner's brain, keyed like every brain neuron.
internal static class AgentFacts
{
    public const string Scope = "owner";
    public static string Key(string name) => Scope + "/agents/" + name;

    public static TestBrain Builder(IChatClient model) => TestBrain.Create().WithModule<AIModule>().WithChatModel(model);

    public static Task<ITestBrain> Brain(IChatClient model, CancellationToken ct) => Builder(model).StartAsync(ct);

    public static async Task<List<AgentTurnEvent>> Collect(IAgent agent, AgentTurn turn, CancellationToken ct)
    {
        var events = new List<AgentTurnEvent>();
        await foreach (var item in agent.Run(turn, ct)) { events.Add(item); }
        return events;
    }
}
