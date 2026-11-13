using IntoChat.OS.Authority.Identity.Grants;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Authority.Identity.Directory;

// The deployment's storage identity; the bootstrap owner is provisioned by MachineRegistration (R59).
internal sealed class IdentityStartup(DeploymentStorageIdentity storageIdentity, IEdgeGrantsCache edgeGrants) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = edgeGrants;
        return storageIdentity.BindAsync(cancellationToken);
    }
}
