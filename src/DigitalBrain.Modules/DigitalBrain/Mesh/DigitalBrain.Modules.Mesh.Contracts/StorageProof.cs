using System.Security.Cryptography;

namespace DigitalBrain.Mesh;

// A node proves it still holds a shard by hashing a fresh nonce with it; the expected answer was computed at write time.
public static class StorageProof
{
    public static byte[] Compute(byte[] nonce, byte[] shard)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(shard);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(nonce);
        hash.AppendData(shard);
        return hash.GetHashAndReset();
    }

    public static string ContentId(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
