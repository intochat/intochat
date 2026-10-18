namespace DigitalBrain.Platform.Storage;

// Another process holds the brain's writer lock: this silo does not own the brain and must not open or create its log.
public sealed class BrainNotOwnedException(string brainId, Exception? inner = null)
    : IOException($"Brain '{brainId}' is owned by another writer.", inner)
{
    public string BrainId { get; } = brainId;
}
