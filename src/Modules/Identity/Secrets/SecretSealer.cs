using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Vault;

namespace IntoChat.OS.Modules.Identity.Secrets;

internal sealed class SecretSealer(IKeyWrapper keys) : ISecretSealer
{
    public SealedSecret Seal(string name, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var key = SecretCipher.NewKey();
        return new SealedSecret { KeyId = keys.Wrap(key), Cipher = SecretCipher.Seal(key, plaintext) };
    }

    public string Open(SealedSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SecretCipher.Open(keys.Unwrap(secret.KeyId), secret.Cipher);
    }
}
