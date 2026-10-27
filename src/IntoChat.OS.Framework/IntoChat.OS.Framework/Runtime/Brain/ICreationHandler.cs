using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

// Runs when a brain is created and is awaited by IBrain.Create, so a new brain is complete when
// its creator gets it. Idempotent and independent: one failing does not fail the brain.
public interface ICreationHandler
{
    // brainId names the brain just created (06-08: the brain neuron is plain; handlers no longer receive its grain).
    Task Created(string brainId, Created created, CancellationToken cancellationToken);
}
