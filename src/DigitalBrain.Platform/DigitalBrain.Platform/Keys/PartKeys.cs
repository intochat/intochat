using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Keys;

internal sealed class PartKeys(IKeyringClient keyring, IMasterKeyCustody custody) : IPartKeys
{
    public async Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default)
    {
        if (await keyring.Get(part.BrainId, part.Value) is { } wrapped) { return custody.Unwrap(ownerAccount, Context(part), wrapped); }
        var key = RandomNumberGenerator.GetBytes(32);
        // Two silos racing to create: the first add wins and both use its key.
        return await keyring.TryAdd(part.BrainId, part.Value, custody.Wrap(ownerAccount, Context(part), key))
            ? key
            : custody.Unwrap(ownerAccount, Context(part), (await keyring.Get(part.BrainId, part.Value))!);
    }

    private static string Context(PartId part) => $"part-key|{part.Value}";
}
