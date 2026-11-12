using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Routing;

// A router picks a capability and method from the deployed contract catalog for unstructured
// state, using a System One model. It decides; it never generates prose and it makes no call.
[NeuronAlias("ai.router"), DefaultNeuronType("ai.router")]
public interface IRouter
{
    // The brain's own router (R4-19: a brain-scoped singleton is keyed main); a script may key its own, {brain}/{name}.
    static string Key(string brainId) => $"{brainId}/main";

    // Decide only: the catalog contract, method and confidence. No call is made.
    [DigitalBrain.Contracts.Interleaves] Task<RouteResult> Route(string state, string[]? allowed = null, CancellationToken ct = default);
}
