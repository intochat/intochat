namespace IntoChat.Cloud.Mail.Templates.Tests.Unit;

public sealed class InviteTokensTests
{
    [Fact]
    public void NewToken_Generated_IsUrlSafeWellFormedAndUnique()
    {
        var first = InviteTokens.NewToken();
        var second = InviteTokens.NewToken();

        Assert.True(InviteTokens.IsWellFormed(first));
        Assert.Equal(43, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Hash_Token_IsLowercaseHexAndDiffersFromToken()
    {
        var token = InviteTokens.NewToken();

        var hash = InviteTokens.Hash(token);

        Assert.True(InviteTokens.IsHash(hash));
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
        Assert.Equal(hash, InviteTokens.Hash(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("a+b")]
    [InlineData("a=b")]
    public void IsWellFormed_OutsideAlphabet_ReturnsFalse(string token) =>
        Assert.False(InviteTokens.IsWellFormed(token));

    [Fact]
    public void IsWellFormed_TooLong_ReturnsFalse() =>
        Assert.False(InviteTokens.IsWellFormed(new string('a', InviteTokens.MaxTokenLength + 1)));

    [Fact]
    public void LinkFor_Token_PutsTokenInFragmentOnly()
    {
        var link = InviteTokens.LinkFor("https://app.intochat.io/", "abc_DEF-123");

        Assert.Equal("https://app.intochat.io/invite#abc_DEF-123", link);
        Assert.True(InviteTokens.TryReadToken(link, out var token));
        Assert.Equal("abc_DEF-123", token);
        Assert.DoesNotContain("abc_DEF-123", MailLogRedaction.RedactUrl(link), StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadToken_NoFragment_ReturnsFalse() =>
        Assert.False(InviteTokens.TryReadToken("https://app.intochat.io/invite", out _));
}
