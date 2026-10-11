using DigitalBrain.Platform.Keys;

namespace DigitalBrain.Platform.Trust;

internal interface IKeyringClient
{
    Task<byte[]?> Get(string brainId, string partId);

    Task<bool> TryAdd(string brainId, string partId, byte[] wrapped);

    // T26-5: a circle part key per contributed machine and generation. A machine reads only its own wraps.
    Task<CircleKeyWrap[]> WrapsOf(string brainId, string partId, string machineId);

    Task<long> Generation(string brainId, string partId);

    Task<string[]> CircleParts(string brainId);

    Task<bool> AddWraps(string brainId, string partId, long generation, CircleKeyWrap[] wraps);

    // The account that owns a brain, from its authority on the trust side (R36); the keyring keeps no owner.
    Task<string?> OwnerOf(string brainId);
}
