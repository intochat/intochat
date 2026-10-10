using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class ObjectSealFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void SealedBytesOpenWithTheSameKeyAndContainNoPlaintext()
    {
        var plain = Encoding.UTF8.GetBytes("timeline: ten clips, private");
        var cipher = ObjectSeal.Seal(Key, "vlad/apps/filmcraft/doc", 1, plain, out var nonce);

        Assert.DoesNotContain("timeline", Encoding.UTF8.GetString(cipher), StringComparison.Ordinal);
        Assert.Equal(plain, ObjectSeal.Open(Key, "vlad/apps/filmcraft/doc", 1, nonce, cipher));
    }

    [Fact]
    public void AWrongKeyFailsInsteadOfReturningGarbage()
    {
        var cipher = ObjectSeal.Seal(Key, "o", 1, [1, 2, 3], out var nonce);
        Assert.ThrowsAny<CryptographicException>(() => ObjectSeal.Open(RandomNumberGenerator.GetBytes(32), "o", 1, nonce, cipher));
    }

    [Fact]
    public void ReplayingAnOlderVersionUnderANewerVersionNumberFails()
    {
        var cipher = ObjectSeal.Seal(Key, "o", 1, [1, 2, 3], out var nonce);
        Assert.ThrowsAny<CryptographicException>(() => ObjectSeal.Open(Key, "o", 2, nonce, cipher));
    }

    [Fact]
    public void AShortKeyIsRefused() =>
        Assert.Throws<ArgumentException>(() => ObjectSeal.Seal(new byte[16], "o", 1, [1], out _));

    [Fact]
    public void AProofCannotBeAnsweredWithoutTheShard()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        Assert.NotEqual(StorageProof.Compute(nonce, [1, 2, 3]), StorageProof.Compute(nonce, [1, 2, 4]));
        Assert.StartsWith("sha256:", StorageProof.ContentId([1, 2, 3]), StringComparison.Ordinal);
    }
}
