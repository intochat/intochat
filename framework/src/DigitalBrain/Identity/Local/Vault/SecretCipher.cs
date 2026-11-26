using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>AES-GCM secret cipher used by the local vault and master-key sealing helpers.</summary>
public static class SecretCipher
{
    /// <summary>Key length in bytes (256-bit AES).</summary>
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Generates a random 256-bit symmetric key.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Encrypts <paramref name="plaintext"/> using AES-GCM with <paramref name="key"/>.</summary>
    public static string Seal(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        var combined = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, NonceSize);
        cipher.CopyTo(combined, NonceSize + TagSize);
        return Convert.ToBase64String(combined);
    }

    /// <summary>Decrypts <paramref name="sealedText"/> produced by <see cref="Seal"/>.</summary>
    public static string Open(byte[] key, string sealedText)
    {
        var sealedBytes = Convert.FromBase64String(sealedText);
        if (sealedBytes.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Truncated sealed secret.");
        }

        var nonce = sealedBytes.AsSpan(0, NonceSize);
        var tag = sealedBytes.AsSpan(NonceSize, TagSize);
        var cipher = sealedBytes.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
