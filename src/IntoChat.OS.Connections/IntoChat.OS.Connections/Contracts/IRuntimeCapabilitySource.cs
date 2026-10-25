using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Connections.Contracts;

// Capabilities a brain has live beyond its kernel types (the Tools module's MCP and page tool sources,
// asked for their current tools). NeuronTypeSearch ranks them together with the kernel's neuron types.
public interface IRuntimeCapabilitySource
{
    Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct);
}
