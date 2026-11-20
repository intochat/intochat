// os-only: os per-person brain membership seam answered by Identity.

namespace IntoChat.OS.Host.Brains;

// The brains a person is a member of, read by Kernel.Brains(). The identity directory answers it (Connections);
// a host with no membership source refuses to list brains rather than listing every brain.
public interface IBrainMembership
{
    Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken = default);
}
