using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// The brains a person is a member of, read by Kernel.Brains(). The identity directory answers it (Connections);
// a host with no membership source refuses to list brains rather than listing every brain.
public interface IBrainMembership
{
    Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken = default);
}
