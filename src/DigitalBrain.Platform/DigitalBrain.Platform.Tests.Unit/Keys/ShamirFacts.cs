using System.Security.Cryptography;
using DigitalBrain.Platform.Keys;

namespace DigitalBrain.Platform.Tests.Unit.Keys;

public sealed class ShamirFacts
{
    [Fact]
    public void AnyTwoOfThreeSharesRecoverTheKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var shares = Shamir.Split(key, 2, 3);
        Assert.Equal(key, Shamir.Combine([shares[0], shares[1]]));
        Assert.Equal(key, Shamir.Combine([shares[0], shares[2]]));
        Assert.Equal(key, Shamir.Combine([shares[1], shares[2]]));
    }

    [Fact]
    public void OneShareAloneDoesNotRecoverTheKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(key, Shamir.Combine([Shamir.Split(key, 2, 3)[0]]));
    }

    [Fact]
    public void AThresholdAboveTheShareCountIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Shamir.Split(new byte[32], 4, 3));
}
