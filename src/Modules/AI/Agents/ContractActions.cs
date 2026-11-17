using System.ComponentModel;
using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents;

// The model's only actions: the neuron surface over the contracts the app composed. Each call runs
// as the agent, marked model-originated so neurons can withhold what the model must not see.
internal static class ContractActions
{
    public static IReadOnlyList<AIFunction> Create(OsNeuronSurface surface, string conversation) =>
    [
        AIFunctionFactory.Create(([Description("Optional full contract type name to list one type only.")] string? contract = null, CancellationToken ct = default) =>
            Marked(conversation, async () => await surface.List(contract, ct)), NeuronSurfaceTools.ListName, NeuronSurfaceTools.ListDescription),
        AIFunctionFactory.Create(([Description("What you want to do.")] string query, CancellationToken ct) =>
            Marked(conversation, async () => await surface.Search(query, ct)), NeuronSurfaceTools.SearchName, NeuronSurfaceTools.SearchDescription),
        AIFunctionFactory.Create((string contract, string key, CancellationToken ct) =>
            Marked(conversation, async () => await surface.Get(contract, key, ct)), NeuronSurfaceTools.GetName, NeuronSurfaceTools.GetDescription),
        AIFunctionFactory.Create((
            string contract,
            string key,
            string method,
            [Description("JSON array of method arguments, e.g. [\"arg1\"] or \"[\\\"arg1\\\"]\" (omit or [] for 0-argument methods).")] JsonElement? arguments = null,
            CancellationToken ct = default) =>
            Marked(conversation, async () => (object?)await surface.Call(contract, key, method, NormalizeArguments(arguments), ct) ?? new { ok = true }),
            NeuronSurfaceTools.CallName, NeuronSurfaceTools.CallDescription),
        AIFunctionFactory.Create(() => Marked(conversation, () => Task.FromResult<object>(RuntimeCallerContext.Require())),
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

    // The model acts as the agent: a grant-checked caller of its own, whose denials come back to it as error results.
    internal static async Task<object> Marked(string conversation, Func<Task<object>> action)
    {
        var caller = RuntimeCallerContext.Require();
#pragma warning disable CS0618 // the only AppId that yields CallerKind.Agent; digitalbrain follow-up to un-obsolete it
        RuntimeCallerContext.Stamp(caller with { AppId = CapabilityCallers.AssistantApp, ConversationId = conversation });
#pragma warning restore CS0618
        try { return await action(); }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or DigitalBrain.Rules.RuleViolationException)
        { return new { isError = true, message = error.Message }; }
        finally { RuntimeCallerContext.Stamp(caller); }
    }
}
