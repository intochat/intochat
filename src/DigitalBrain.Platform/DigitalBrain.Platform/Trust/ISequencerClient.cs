using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Trust;

// Only wrapped keys, signed heads, sealed shards, machine public keys and signed payloads cross the trust clients.
internal interface ISequencerClient
{
    Task<HeadRecord?> Get(string partHash);

    // R154: partId is the PartId value whose hash partHash is; a circle part's head is accepted only from a contributed machine.
    Task<bool> CompareAndSet(string partHash, long expectedVersion, HeadRecord next, string partId);

    // R153: ends a withdrawn machine's lease on a circle part; honoured only from a machine contributed to that circle.
    Task Revoke(string circleBrainId, string partId, string machineId);
}
