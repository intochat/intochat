// os-only: the os seam asking Identity whether a principal may reach a brain.
namespace IntoChat.OS.Framework.Modules;

// Modules that own brain-scoped HTTP routes ask this seam whether the stamped principal may
// reach a brain. Identity registers the directory-backed implementation.
public interface IBrainAccess
{
    ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default);
}
