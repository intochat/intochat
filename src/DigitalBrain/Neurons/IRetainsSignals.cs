using Orleans.Concurrency;

namespace DigitalBrain;

// A neuron that keeps a bounded buffer of its recent signals, so a subscription created after they
// were published still receives them. The brain runtime seeds a new subscription's backlog from here;
// a neuron that does not retain contributes nothing. Delivery stays at-least-once: deduplicating
// against its own acknowledged facts stays the synapse's job.
[Alias("brain.retained-signals")]
public interface IRetainsSignals : INeuron
{
    // The retained signals of kind `signal` (the signal's type name, as a subscription stores it)
    // whose sequence is newer than `since` - the subscription's watermark, zero for a new one - in
    // sequence order. Empty when this neuron retains no such signal. A pure read of the buffer, so it
    // interleaves: the runtime seeding a subscription must not wait out a turn of the source, which may
    // itself be waiting on the runtime.
    [ReadOnly, AlwaysInterleave]
    Task<RetainedSignal[]> Retained(string signal, long since);
}
