using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class PurposeHasherTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Hash_SamePurposeAndValue_IsDeterministic()
    {
        var hasher = new PurposeHasher("codes", Key);

        Assert.Equal(hasher.Hash("abc"), hasher.Hash("abc"));
        Assert.StartsWith("ph1:codes:", hasher.Hash("abc"), StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_DifferentPurposes_Differ()
    {
        var a = new PurposeHasher("codes", Key).Hash("abc");
        var b = new PurposeHasher("tokens", Key).Hash("abc");

        Assert.NotEqual(a["ph1:codes:".Length..], b["ph1:tokens:".Length..]);
    }

    [Fact]
    public void Verify_MatchingValue_ReturnsTrue()
    {
        var hasher = new PurposeHasher("codes", Key);

        Assert.True(hasher.Verify("abc", hasher.Hash("abc")));
        Assert.False(hasher.Verify("abd", hasher.Hash("abc")));
    }

    [Fact]
    public void Verify_ValueWithoutPrefix_IsRejected()
    {
        var hasher = new PurposeHasher("codes", Key);
        var unprefixed = hasher.Hash("abc")["ph1:codes:".Length..];

        Assert.False(hasher.Verify("abc", unprefixed));
    }

    [Fact]
    public void Verify_OtherPurposesPrefix_IsRejected()
    {
        var codes = new PurposeHasher("codes", Key);
        var tokens = new PurposeHasher("tokens", Key);

        Assert.False(codes.Verify("abc", tokens.Hash("abc")));
    }

    [Fact]
    public void Constructor_ShortRootKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PurposeHasher("codes", new byte[16]));
    }
}