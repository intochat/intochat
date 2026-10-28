using IntoChat.OS.Runtime;
using DigitalBrain.Spi;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Modules.Apps;

// Whether some connection in a brain provides a capability. Install asks this before anything lands; the runtime
// capability broker (05-07) replaces the transitional implementation below behind the same seam.
public interface ICapabilitySatisfaction
{
    Task<bool> Satisfied(string brain, string capability, CancellationToken ct);
}

// A connected connection whose module provides the capability (ConnectionSummary.Provides, read from the module's
// ModuleManifest, 05-07); a pending, expired or failing connection satisfies nothing (D-10).
public sealed class ConnectedModuleSatisfaction(INeuronProxies brains) : ICapabilitySatisfaction
{
    public async Task<bool> Satisfied(string brain, string capability, CancellationToken ct)
        => Satisfies(await brains.Get<IConnectionRequests>(IConnectionRequests.Key(brain)).ListConnections().WaitAsync(ct), capability);

    public static bool Satisfies(IEnumerable<ConnectionSummary> connections, string capability)
        => connections.Any(connection => connection.Status == "connected" && AppCapabilities.Satisfies(connection.Provides, capability));
}
