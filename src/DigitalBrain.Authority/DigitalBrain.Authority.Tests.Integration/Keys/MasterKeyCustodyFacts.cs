using System.Security.Cryptography;
using DigitalBrain.Authority.Keys;

namespace DigitalBrain.Authority.Tests.Integration.Keys;

public sealed class MasterKeyCustodyFacts
{
    private static ConfiguredMasterKeyCustody Custody() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void AWrappedSecretUnwrapsForTheSameAccountAndContext()
    {
        var custody = Custody();
        var secret = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(secret, custody.Unwrap("alice", "part-key|home/core", custody.Wrap("alice", "part-key|home/core", secret)));
    }

    [Fact]
    public void AnotherAccountOrContextCannotUnwrapIt()
    {
        var custody = Custody();
        var wrapped = custody.Wrap("alice", "part-key|home/core", [1, 2, 3]);
        Assert.ThrowsAny<CryptographicException>(() => custody.Unwrap("bob", "part-key|home/core", wrapped));
        Assert.ThrowsAny<CryptographicException>(() => custody.Unwrap("alice", "part-key|home/apps/x", wrapped));
    }
}
