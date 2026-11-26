using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps;

// The Creator's two app calls as the catalog's HTTP surface and the app studio use them. The calls reach the app through the
// caller's own proxies, so each is made as that caller.
public interface ICreator
{
    // With no name, the Author names the app and it exists, Drafted, when this returns. With a name, it reshapes that app's spec.
    Task<AppSnapshot> Author(INeuronProxies brain, string brainId, string? name, string request, CancellationToken cancellationToken);

    // Flips the app to Building, starts its build flow and returns; the flow authors, drafts, verifies, deploys and installs the unit.
    Task<AppSnapshot> Implement(INeuronProxies brain, string appKey, string? instruction, CancellationToken cancellationToken = default);
}
