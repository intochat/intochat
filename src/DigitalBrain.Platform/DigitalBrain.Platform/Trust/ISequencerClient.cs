using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Trust;

// Only wrapped keys, signed heads, sealed shards, machine public keys and signed payloads cross the trust clients.
internal interface ISequencerClient
{
    Task<HeadRecord?> Get(string partHash);

    Task<bool> CompareAndSet(string partHash, long expectedVersion, HeadRecord next);
}
