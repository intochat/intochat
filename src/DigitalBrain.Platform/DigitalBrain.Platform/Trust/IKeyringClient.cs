namespace DigitalBrain.Platform.Trust;

internal interface IKeyringClient
{
    Task<byte[]?> Get(string brainId, string partId);

    Task<bool> TryAdd(string brainId, string partId, byte[] wrapped);

    // The account that owns a brain, from its authority on the trust side (R36); the keyring keeps no owner.
    Task<string?> OwnerOf(string brainId);
}
