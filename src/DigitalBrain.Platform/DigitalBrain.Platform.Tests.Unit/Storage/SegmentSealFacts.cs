using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Platform.Storage;

namespace DigitalBrain.Platform.Tests.Unit.Storage;

public sealed class SegmentSealFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void SealedBytesOpenWithTheSameKeyAndContainNoPlaintext()
    {
        var plain = Encoding.UTF8.GetBytes("timeline: ten clips, private");
        var sealedBytes = SegmentSeal.Close(Key, "vlad/apps/filmcraft/log/1", plain);

        Assert.DoesNotContain("timeline", Encoding.UTF8.GetString(sealedBytes), StringComparison.Ordinal);
        Assert.Equal(plain, SegmentSeal.Open(Key, "vlad/apps/filmcraft/log/1", sealedBytes));
    }

    [Fact]
    public void AWrongKeyFailsInsteadOfReturningGarbage() =>
        Assert.ThrowsAny<CryptographicException>(() => SegmentSeal.Open(RandomNumberGenerator.GetBytes(32), "s", SegmentSeal.Close(Key, "s", [1, 2, 3])));

    [Fact]
    public void ReplayingASegmentAsAnotherFails() =>
        Assert.ThrowsAny<CryptographicException>(() => SegmentSeal.Open(Key, "s/2", SegmentSeal.Close(Key, "s/1", [1, 2, 3])));

    [Fact]
    public void TruncatedBytesFailAuthentication() =>
        Assert.ThrowsAny<CryptographicException>(() => SegmentSeal.Open(Key, "s", new byte[10]));

    [Fact]
    public void AShortKeyIsRefused() =>
        Assert.Throws<ArgumentException>(() => SegmentSeal.Close(new byte[16], "s", [1]));

}
