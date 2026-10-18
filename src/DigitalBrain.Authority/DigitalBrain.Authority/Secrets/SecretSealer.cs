using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Authority.Secrets;

internal sealed class SecretSealer(IKeyWrapper keys) : ISecretSealer
{
    public SealedSecret Seal(string name, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var key = SecretCipher.NewKey();
        return new SealedSecret(name, keys.Wrap(key), SecretCipher.Seal(key, plaintext));
    }

    public string Open(SealedSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SecretCipher.Open(keys.Unwrap(secret.KeyId), secret.Ciphertext);
    }
}
