using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Mesh;

// AES-256-GCM with the caller's key. The object id and version are authenticated, so a shard set cannot be
// replayed as another object or as a different version of the same one.
internal static class ObjectSeal
{
    private const int TagSize = 16;

    public static byte[] Seal(byte[] key, string objectId, long version, byte[] plaintext, out byte[] nonce)
    {
        RequireKey(key);
        ArgumentNullException.ThrowIfNull(plaintext);
        nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher.AsSpan(0, plaintext.Length), cipher.AsSpan(plaintext.Length), Aad(objectId, version));
        return cipher;
    }

    public static byte[] Open(byte[] key, string objectId, long version, byte[] nonce, byte[] cipher)
    {
        RequireKey(key);
        ArgumentNullException.ThrowIfNull(cipher);
        var plain = new byte[cipher.Length - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, Aad(objectId, version));
        return plain;
    }

    private static void RequireKey(byte[] key)
    {
        if (key is not { Length: 32 }) { throw new ArgumentException("A mesh object key is 32 bytes.", nameof(key)); }
    }

    private static byte[] Aad(string objectId, long version) => Encoding.UTF8.GetBytes($"{objectId}|{version}");
}
