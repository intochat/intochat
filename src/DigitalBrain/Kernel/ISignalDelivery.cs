namespace DigitalBrain;

// In-process publish returns after it queues. A coalesce drain waits until those queued signals have
// reached the subscriber channel, so a burst posted during a rebuild is one following rebuild.
internal interface ISignalDelivery
{
    Task DeliveredAsync(CancellationToken cancellationToken);
}
