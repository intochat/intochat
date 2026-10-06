using DigitalBrain;

namespace DigitalBrain.Framework;

// What a new brain is born with: Establish awaits every provisioner before it returns, so whoever
// creates a brain gets one that is already complete. Provisioners are idempotent; one that fails
// does not fail the brain, and Activated handlers may catch up later.
public interface IBrainProvisioner
{
    Task Provision(Activated brain, CancellationToken cancellationToken);
}
