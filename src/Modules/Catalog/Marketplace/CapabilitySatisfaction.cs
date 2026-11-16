using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Apps;

// A connected connection whose module provides the capability (ConnectionSummary.Provides, read from the module's
// ModuleManifest, 05-07); a pending, expired or failing connection satisfies nothing (D-10).
internal sealed class ConnectedModuleSatisfaction(INeuronProxies brains) : ICapabilitySatisfaction
{
    public async Task<bool> Satisfied(string brain, string capability, CancellationToken ct)
        => Satisfies(await brains.Get<IConnectionRequests>(IConnectionRequests.Key(brain)).ListConnections().WaitAsync(ct), capability);

    public static bool Satisfies(IEnumerable<ConnectionSummary> connections, string capability)
        => connections.Any(connection => connection.Status == "connected" && AppCapabilities.Satisfies(connection.Provides, capability));
}
