using System.ComponentModel;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Host.Surface;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

// The model's actions: meta discovery tools (neurons_search, neurons_list, neurons_context) plus one typed
// ContractTool per method of every contract the agent composes. Each call runs as the agent for the person.
internal static class AgentTools
{
    public const string SearchName = NeuronSurfaceTools.SearchName;

    public static IReadOnlyList<AIFunction> Meta(EdgeAuthority? edge, OsNeuronSurface surface, string conversation) =>
    [
        AIFunctionFactory.Create(
            ([Description("What you want to do.")] string query, CancellationToken ct) =>
                Marked(edge, conversation, async () => await surface.Search(query, ct).ConfigureAwait(false)),
            NeuronSurfaceTools.SearchName,
            NeuronSurfaceTools.SearchDescription),
        AIFunctionFactory.Create(
            ([Description("Optional full contract type name to list one type only.")] string? contract = null, CancellationToken ct = default) =>
                Marked(edge, conversation, async () => await surface.List(contract, ct).ConfigureAwait(false)),
            NeuronSurfaceTools.ListName,
            NeuronSurfaceTools.ListDescription),
        AIFunctionFactory.Create(
            () => Marked(edge, conversation, () => Task.FromResult<object>(Describe(edge?.Current))),
            NeuronSurfaceTools.ContextName,
            NeuronSurfaceTools.ContextDescription),
    ];

    public static IReadOnlyList<ContractTool> For(EdgeAuthority? edge, OsNeuronSurface surface, IReadOnlyList<Type> contracts, string conversation)
    {
        var tools = new List<ContractTool>();
        foreach (var contract in contracts)
        {
            foreach (var schema in ToolSchemas.Of(contract))
            {
                tools.Add(new ContractTool(schema, edge, surface, conversation));
            }
        }
        return tools;
    }

    // What the model may know about whom it acts as: never a stamp it could replay.
    private static object Describe(Caller? caller) => new
    {
        actor = caller?.Actor.GetType().Name,
        actsFor = CallerRules.ActsFor(caller),
        brain = caller?.Brain.Value,
        conversation = caller?.Conversation,
    };

    // The model acts as the agent for the person the turn runs for: a grant-checked caller of its own, whose denials come back to
    // it as error results. A turn with no person behind it has no agent to act as and is refused.
    internal static async Task<object> Marked(EdgeAuthority? edge, string conversation, Func<Task<object>> action)
    {
        var caller = edge?.Current ?? throw new UnauthorizedAccessException("An agent tool call needs the stamped caller of its turn.");
        var person = caller.For ?? throw new UnauthorizedAccessException("An agent acts only for a person.");
        using var asAgent = edge.Enter(edge.Agent(person, caller.Brain, conversation));
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or DigitalBrain.Rules.RuleViolationException)
        { return new { isError = true, message = error.Message }; }
    }
}
