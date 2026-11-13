using System.Text.RegularExpressions;
using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class RecoveryCodesTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Generate_ReturnsTenWellFormedCodesAndHashes()
    {
        var (codes, hashes) = new RecoveryCodes(Key).Generate();

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, hashes.Count);
        Assert.All(codes, code => Assert.Matches(new Regex("^[a-hj-km-np-tv-z2-9]{5}-[a-hj-km-np-tv-z2-9]{5}$"), code));
        Assert.All(hashes, hash => Assert.StartsWith("ph1:auth-recovery:", hash, StringComparison.Ordinal));
    }

    [Fact]
    public void Use_MatchingCode_RemovesOnlyThatHash()
    {
        var recovery = new RecoveryCodes(Key);
        var (codes, hashes) = recovery.Generate();

        var result = recovery.Use(codes[3], hashes);

        Assert.True(result.Matched);
        Assert.Equal(9, result.Remaining.Count);
        Assert.DoesNotContain(hashes[3], result.Remaining);
    }

    [Fact]
    public void Use_SecondUse_Fails()
    {
        var recovery = new RecoveryCodes(Key);
        var (codes, hashes) = recovery.Generate();
        var first = recovery.Use(codes[0], hashes);

        var second = recovery.Use(codes[0], first.Remaining);

        Assert.False(second.Matched);
        Assert.Equal(first.Remaining, second.Remaining);
    }

    [Fact]
    public void Use_Miss_ReturnsHashesUnchanged()
    {
        var recovery = new RecoveryCodes(Key);
        var (_, hashes) = recovery.Generate();

        var result = recovery.Use("aaaaa-aaaaa", hashes);

        Assert.False(result.Matched);
        Assert.Equal(hashes, result.Remaining);
    }

    [Fact]
    public void Use_IgnoresCaseAndDash()
    {
        var recovery = new RecoveryCodes(Key);
        var (codes, hashes) = recovery.Generate();

        Assert.True(recovery.Use(codes[1].ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal), hashes).Matched);
    }
}
