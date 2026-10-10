using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DigitalBrain.Platform.Mesh;

// A node proves it still holds a shard by hashing a fresh nonce with it; the expected answer was computed at
// write time and travels sealed in the manifest. The nonce is length-prefixed so nonce and shard cannot trade bytes.
internal static class StorageProof
{
    public static byte[] Compute(byte[] nonce, byte[] shard)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(shard);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, nonce.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(length);
        hash.AppendData(nonce);
        hash.AppendData(shard);
        return hash.GetHashAndReset();
    }

    public static string ContentId(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
