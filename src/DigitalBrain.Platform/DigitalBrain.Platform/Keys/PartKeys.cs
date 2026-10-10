using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Keys;

// R74: one key per part. The keyring's TryAdd is its only creator: a machine that loses the race re-reads the
// winner's wrapped key, so every machine of the owner opens the part under the same key.
internal sealed class PartKeys(IKeyringClient keyring, IMasterKeyCustody custody) : IPartKeys
{
    public async Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default)
    {
        if (await keyring.Get(part.BrainId, part.Value) is { } wrapped) { return Unwrap(part, ownerAccount, wrapped); }
        var key = RandomNumberGenerator.GetBytes(32);
        return await keyring.TryAdd(part.BrainId, part.Value, custody.Wrap(ownerAccount, Context(part), key))
            ? key
            : Unwrap(part, ownerAccount, (await keyring.Get(part.BrainId, part.Value))!);
    }

    // A custody that is not the owner's (another root) cannot open the wrapped key: the part is refused here,
    // before any of its sealed bytes are read under a wrong key.
    private byte[] Unwrap(PartId part, string ownerAccount, byte[] wrapped)
    {
        try { return custody.Unwrap(ownerAccount, Context(part), wrapped); }
        catch (CryptographicException error)
        { throw new ServiceUnavailableException($"The key of part {part} is not unwrappable on this machine: its custody is not the owner's ({error.Message})."); }
    }

    private static string Context(PartId part) => $"part-key|{part.Value}";
}
