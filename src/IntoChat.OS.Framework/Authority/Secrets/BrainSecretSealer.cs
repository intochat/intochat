using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Authority.Storage;

namespace IntoChat.OS.Authority.Secrets;

internal sealed class BrainSecretSealer(BrainStorageKeys keys) : IBrainSecretSealer
{
    public Task<SealedSecret> Seal(string brainId, string name, string plaintext)
        => Seal(brainId, name, plaintext, CancellationToken.None);

    public async Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var key = await keys.GetOrCreate(brainId, cancellationToken);
        var data = SecretCipher.NewKey();
        return new SealedSecret
        {
            KeyId = BrainSeal.KeyPrefix + SecretCipher.Seal(key, Convert.ToBase64String(data)),
            Cipher = SecretCipher.Seal(data, plaintext),
        };
    }

    public Task<string> Open(string brainId, SealedSecret secret)
        => Open(brainId, secret, CancellationToken.None);

    public async Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!BrainSeal.IsBrainSealed(secret)) { throw new ArgumentException("A brain-sealed secret is required.", nameof(secret)); }
        var key = await keys.GetOrCreate(brainId, cancellationToken);
        var data = Convert.FromBase64String(SecretCipher.Open(key, secret.KeyId[BrainSeal.KeyPrefix.Length..]));
        return SecretCipher.Open(data, secret.Cipher);
    }
}
