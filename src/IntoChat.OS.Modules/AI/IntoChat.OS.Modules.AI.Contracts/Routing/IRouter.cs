using Orleans.Concurrency;

namespace IntoChat.OS.Modules.AI.Routing;

// A router picks a capability and method from the deployed contract catalog for unstructured
// state, using a System One model, then dispatches the call. It decides; it never generates prose.
[Alias("ai.router"), Orleans.Metadata.DefaultGrainType("ai.router")]
public interface IRouter : INeuron
{
    // Record the request and return; the router answers by writing the result into its state and
    // publishing Routed, so a slow model holds no caller.
    Task Ask(RouteRequest request, CancellationToken ct = default);

    [ReadOnly, AlwaysInterleave] Task<PendingRoute?> Pending(Guid id, CancellationToken ct = default);

    // Decide only, for callers that can wait: the catalog contract, method and confidence. No call is made.
    [AlwaysInterleave] Task<RouteResult> Route(string state, string[]? allowed = null, CancellationToken ct = default);

    // Decide, fill the call's key and arguments with a language model, and invoke the chosen neuron.
    [AlwaysInterleave] Task<RouteOutcome> Dispatch(string state, string[]? allowed = null, CancellationToken ct = default);

    // Achieve a goal: pick a capability, carry it out, feed the result back, and repeat until the
    // goal is done. This is the router acting as the person's assistant across several actions.
    [AlwaysInterleave, ResponseTimeout("00:15:00")] Task<GoalRun> Achieve(string goal, string[]? allowed = null, CancellationToken ct = default);
}
