using System.Security.Cryptography;
using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class EmailNormalizerTests
{
    [Fact]
    public void Normalize_TrimsAndLowercases()
    {
        Assert.Equal("user@example.com", EmailNormalizer.Normalize("  User@Example.COM "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public void Normalize_Invalid_ReturnsNull(string? input)
    {
        Assert.Null(EmailNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_Over255Chars_ReturnsNull()
    {
        var local = new string('a', 250);

        Assert.Null(EmailNormalizer.Normalize(local + "@example.com"));
        Assert.NotNull(EmailNormalizer.Normalize(new string('a', 243) + "@example.com"));
    }

    [Fact]
    public void CodeId_RoundTrips()
    {
        var subject = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(8);

        var id = CodeId.Encode(CodePurpose.Reset, subject, nonce);

        Assert.True(CodeId.TryParse(id, out var purpose, out var s, out var n));
        Assert.Equal(CodePurpose.Reset, purpose);
        Assert.Equal(subject, s);
        Assert.Equal(nonce, n);
        Assert.DoesNotContain('=', id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not base64!!!")]
    [InlineData("AAAA")]
    public void CodeId_Malformed_IsRejected(string? value)
    {
        Assert.False(CodeId.TryParse(value, out _, out _, out _));
    }

    [Fact]
    public void CodeId_UnknownPurposeByte_IsRejected()
    {
        var bytes = new byte[25];
        bytes[0] = 99;
        var id = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(CodeId.TryParse(id, out _, out _, out _));
    }
}
