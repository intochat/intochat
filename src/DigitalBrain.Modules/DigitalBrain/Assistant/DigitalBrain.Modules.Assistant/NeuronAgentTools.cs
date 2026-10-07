using System.ComponentModel;
using DigitalBrain.AI.Tools;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Assistant;

// The assistant acts through the same neuron surface digitalbrain-mcp serves; the caller is
// whoever runs the turn (the assistant app), and each neuron's own access decides.
internal sealed class NeuronAgentTools(NeuronSurface surface) : IAgentToolFactory
{
    public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context) =>
    [
        AIFunctionFactory.Create(() => surface.List(), NeuronSurfaceTools.ListName, NeuronSurfaceTools.ListDescription),
        AIFunctionFactory.Create(([Description("What you want to do.")] string query, CancellationToken ct) =>
            Guard(async () => await surface.Search(query, ct)), NeuronSurfaceTools.SearchName, NeuronSurfaceTools.SearchDescription),
        AIFunctionFactory.Create((string contract, string key) =>
            Guard(() => Task.FromResult<object>(surface.Get(contract, key))), NeuronSurfaceTools.GetName, NeuronSurfaceTools.GetDescription),
        AIFunctionFactory.Create((string contract, string key, string method, string arguments, CancellationToken ct) =>
            Guard(async () => (object?)await surface.Call(contract, key, method, arguments, ct) ?? new { ok = true }),
            NeuronSurfaceTools.CallName, NeuronSurfaceTools.CallDescription),
        AIFunctionFactory.Create(() => CallerContextStamper.Require(), NeuronSurfaceTools.ContextName, NeuronSurfaceTools.ContextDescription),
    ];

    // A refused or malformed call is an answer the model can repair, not a failed turn.
    private static async Task<object> Guard(Func<Task<object>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        { return new { isError = true, message = error.Message }; }
    }
}
