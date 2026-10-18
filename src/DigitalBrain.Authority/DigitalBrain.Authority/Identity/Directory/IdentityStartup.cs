using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Authority.Identity.Directory;

// The deployment's storage identity; the bootstrap owner is provisioned by MachineRegistration (R59).
internal sealed class IdentityStartup(DeploymentStorageIdentity storageIdentity) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken) => storageIdentity.BindAsync(cancellationToken);
}
