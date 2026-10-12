using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Secrets;

// R130: seals a shared connection's credential under the core part key of the brain it lives in, so any machine
// holding that part opens it. A circle brain's key is wrapped to its contributed machines (CirclePartKeys); an
// owner's brain key is wrapped under the owner's custody (IPartKeys). The trust side registers none of this.
internal sealed class PartKeySecretSealer(IPartKeys parts, CirclePartKeys circles, IKeyringClient keyring, IIdentityClient identity) : IPartSecretSealer
{
    public async Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var key = await CoreKey(brainId, cancellationToken);
        var data = SecretCipher.NewKey();
        return new SealedSecret(name, PartSeal.Prefix + SecretCipher.Seal(key, Convert.ToBase64String(data)), SecretCipher.Seal(data, plaintext));
    }

    public async Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!PartSeal.IsPartSealed(secret)) { throw new ArgumentException("A part-sealed secret is required.", nameof(secret)); }
        var key = await CoreKey(brainId, cancellationToken);
        return SecretCipher.Open(Unwrap(key, secret.KeyId), secret.Ciphertext);
    }

    private static byte[] Unwrap(byte[] partKey, string keyId)
        => Convert.FromBase64String(SecretCipher.Open(partKey, keyId[PartSeal.Prefix.Length..]));

    private async Task<byte[]> CoreKey(string brainId, CancellationToken cancellationToken)
    {
        var part = PartId.Core(brainId);
        if (await identity.KindOf(brainId) == BrainKind.Circle) { return await circles.Unwrap(part, brainId, cancellationToken); }
        var owner = await keyring.OwnerOf(brainId) ?? throw new ServiceUnavailableException($"Brain {brainId} has no owning account; its part key is unavailable.");
        return await parts.Key(part, owner, cancellationToken);
    }
}
