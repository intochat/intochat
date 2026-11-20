using System.ComponentModel;
using System.Text.Json;
using DigitalBrain.Access;
using Microsoft.Extensions.AI;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Modules.AI.Agents;

// The model's only actions: the neuron surface over the contracts the app composed. Each call runs
// as the agent, marked model-originated so neurons can withhold what the model must not see.
internal static class ContractActions
{
    public static IReadOnlyList<AIFunction> Create(EdgeAuthority? edge, OsNeuronSurface surface, string conversation) =>
    [
        AIFunctionFactory.Create(([Description("Optional full contract type name to list one type only.")] string? contract = null, CancellationToken ct = default) =>
            Marked(edge, conversation, async () => await surface.List(contract, ct)), NeuronSurfaceTools.ListName, NeuronSurfaceTools.ListDescription),
        AIFunctionFactory.Create(([Description("What you want to do.")] string query, CancellationToken ct) =>
            Marked(edge, conversation, async () => await surface.Search(query, ct)), NeuronSurfaceTools.SearchName, NeuronSurfaceTools.SearchDescription),
        AIFunctionFactory.Create((string contract, string key, CancellationToken ct) =>
            Marked(edge, conversation, async () => await surface.Get(contract, key, ct)), NeuronSurfaceTools.GetName, NeuronSurfaceTools.GetDescription),
        AIFunctionFactory.Create((
            string contract,
            string key,
            string method,
            [Description("JSON array of method arguments, e.g. [\"arg1\"] or \"[\\\"arg1\\\"]\" (omit or [] for 0-argument methods).")] JsonElement? arguments = null,
            CancellationToken ct = default) =>
            Marked(edge, conversation, async () => (object?)await surface.Call(contract, key, method, NormalizeArguments(arguments), ct) ?? new { ok = true }),
            NeuronSurfaceTools.CallName, NeuronSurfaceTools.CallDescription),
        AIFunctionFactory.Create(() => Marked(edge, conversation, () => Task.FromResult<object>(Describe(edge?.Current))),
            NeuronSurfaceTools.ContextName, NeuronSurfaceTools.ContextDescription),
    ];

    internal static string NormalizeArguments(JsonElement? arguments)
    {
        if (arguments is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "[]";
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            return string.IsNullOrWhiteSpace(raw) ? "[]" : raw;
        }

        return element.GetRawText();
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
        try { return await action(); }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or DigitalBrain.Rules.RuleViolationException)
        { return new { isError = true, message = error.Message }; }
    }
}
