using DigitalBrain.Testing.Aspire;

namespace DigitalBrain.Testing.E2E;

// The E2E brain's lease over the shared host; deleted with this package in Task 10.
public abstract class SharedBrainFixture : SharedBrain<E2EBrain>
{
    protected sealed override E2EBrain Lease(E2EBrain host) => host.Lease();
}
