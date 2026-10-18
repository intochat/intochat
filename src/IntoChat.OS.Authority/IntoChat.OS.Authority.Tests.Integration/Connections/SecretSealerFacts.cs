using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Authority.Secrets;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

public sealed class SecretSealerFacts
{
    private static SecretSealer Sealer(string masterKey = "master-key-one")
        => new(new MasterKeyWrapper(Options.Create(new MasterKeyOptions { MasterKey = masterKey })));

    [Fact]
    public void ASealedSecretOpensToTheOriginalAndNeverContainsIt()
    {
        var sealer = Sealer();
        var sealed_ = sealer.Seal("value", "hunter2");
        Assert.Equal("value", sealed_.Name);
        Assert.StartsWith("mk3:", sealed_.KeyId, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sealed_.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sealed_.KeyId, StringComparison.Ordinal);
        Assert.Equal("hunter2", sealer.Open(sealed_));
    }

    [Fact]
    public void TwoSealsOfTheSameValueDifferAndEachOpens()
    {
        var sealer = Sealer();
        var first = sealer.Seal("value", "same");
        var second = sealer.Seal("value", "same");
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.NotEqual(first.KeyId, second.KeyId);
        Assert.Equal("same", sealer.Open(first));
        Assert.Equal("same", sealer.Open(second));
    }

    [Fact]
    public void ADifferentMasterKeyCannotOpenTheSecret()
    {
        var sealed_ = Sealer("master-key-one").Seal("value", "hunter2");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Sealer("master-key-two").Open(sealed_));
    }

    [Fact]
    public void SealingRefusesAnEmptyValue()
        => Assert.Throws<ArgumentException>(() => Sealer().Seal("value", ""));
}
