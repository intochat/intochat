namespace IntoChat.OS;

// In-process publish returns after it queues. A coalesce drain waits until those queued signals have
// reached the subscriber channel, so a burst posted during a rebuild is one following rebuild.
// Runtime SPI: implemented by the in-process clients (IntoChat.OS.Client, IntoChat.OS.Testing); transitional (Phase 4/6).
public interface ISignalDelivery
{
    Task DeliveredAsync(CancellationToken cancellationToken);
}
