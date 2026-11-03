using DigitalBrain.Capabilities.Connections.Sealing;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Authority.Secrets;

internal sealed class BrainSecretSealer(BrainStorageKeys keys) : IBrainSecretSealer
{
    public async Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var key = await keys.GetOrCreate(brainId, cancellationToken);
        var data = SecretCipher.NewKey();
        return new SealedSecret(name, BrainSeal.Prefix + SecretCipher.Seal(key, Convert.ToBase64String(data)), SecretCipher.Seal(data, plaintext));
    }
    public async Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!BrainSeal.IsBrainSealed(secret)) { throw new ArgumentException("A brain-sealed secret is required.", nameof(secret)); }
        var key = await keys.GetOrCreate(brainId, cancellationToken);
        var data = Convert.FromBase64String(SecretCipher.Open(key, secret.KeyId[BrainSeal.Prefix.Length..]));
        return SecretCipher.Open(data, secret.Ciphertext);
    }
}
