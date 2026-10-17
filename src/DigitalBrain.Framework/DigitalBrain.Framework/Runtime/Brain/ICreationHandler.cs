namespace DigitalBrain.Framework;

// Runs when a brain is created and is awaited by IBrain.Create, so a new brain is complete when
// its creator gets it. Idempotent and independent: one failing does not fail the brain.
public interface ICreationHandler
{
    Task Created(IBrain kernel, Created created, CancellationToken cancellationToken);
}
