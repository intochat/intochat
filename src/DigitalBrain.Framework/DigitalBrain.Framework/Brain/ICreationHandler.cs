namespace DigitalBrain.Framework;

/// <summary>
/// What a new brain is born with. <see cref="IKernel.Create"/> runs every handler and awaits it,
/// so whoever creates a brain gets one that is already complete.
/// </summary>
/// <remarks>
/// Handlers are idempotent and independent: one failing does not fail the brain, and
/// <see cref="Activated"/> handlers may catch up later.
/// </remarks>
public interface ICreationHandler
{
    Task Created(IKernel kernel, Created created, CancellationToken cancellationToken);
}
