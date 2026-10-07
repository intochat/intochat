using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

// Agents in these facts live in the "research" brain, keyed like every brain neuron.
internal static class AgentFacts
{
    public const string Scope = "research";
    public static string Key(string name) => Scope + "/agents/" + name;

    public static Task<ModuleBrain> Brain(IChatClient client, CancellationToken ct, Action<IServiceCollection>? services = null)
        => ModuleTest.Create().WithModule<AIModule>()
            .ConfigureSilo(silo => { silo.Services.AddSingleton(client); services?.Invoke(silo.Services); }).StartAsync(ct);

    public static async Task<List<AgentTurnEvent>> Collect(IAgent agent, AgentTurn turn, CancellationToken ct)
    {
        var events = new List<AgentTurnEvent>();
        await foreach (var item in agent.Run(turn, ct)) { events.Add(item); }
        return events;
    }

    public static void StampUser() => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = "owner",
        AccountId = "owner",
        BrainId = Scope,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });
}
