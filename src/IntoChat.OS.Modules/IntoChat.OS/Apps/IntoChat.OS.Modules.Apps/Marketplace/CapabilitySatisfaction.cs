using IntoChat.OS.Integrations;

namespace IntoChat.OS.Modules.Apps;

// Whether some connection in a brain provides a capability. Install asks this before anything lands; the runtime
// capability broker (05-07) replaces the transitional implementation below behind the same seam.
public interface ICapabilitySatisfaction
{
    Task<bool> Satisfied(string brain, string capability, CancellationToken ct);
}

// Transitional: a connected connection whose module declares the capability. Most os modules have no
// ModuleManifest yet, so the module's declared connection capabilities (ConnectionSummary.Capabilities) are the
// source; a pending, expired or failing connection satisfies nothing.
public sealed class ConnectedModuleSatisfaction(IIntoChatOS brains) : ICapabilitySatisfaction
{
    public async Task<bool> Satisfied(string brain, string capability, CancellationToken ct)
        => Satisfies(await brains.Get<IConnectionRequests>(brain).ListConnections().WaitAsync(ct), capability);

    public static bool Satisfies(IEnumerable<ConnectionSummary> connections, string capability)
        => connections.Any(connection => connection.Status == "connected" && AppCapabilities.Satisfies(connection.Capabilities, capability));
}
