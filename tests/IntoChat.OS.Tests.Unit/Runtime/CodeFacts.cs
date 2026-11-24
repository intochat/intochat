using IntoChat.OS.Runtime;

namespace IntoChat.OS.Tests.Unit.Runtime;

public sealed class CodeFacts
{
    [Fact]
    public void ANewCodeIsTwelveCrockfordCharactersInThreeGroups()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", Code.New().Value);
        }
        Assert.NotEqual(Code.New(), Code.New());
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn", "ABCD-EFGH-JKMN")]
    [InlineData(" ilo0 1111 2222 ", "1100-1111-2222")]
    public void ParsingNormalizes(string input, string expected)
    {
        Assert.True(Code.TryParse(input, out var c));
        Assert.Equal(expected, c.Value);
    }

    [Theory]
    [InlineData("ada.ABCD-EFGH-JKMN")]
    [InlineData("ABCD-EFGH-JKMU")]
    [InlineData("")]
    [InlineData(null)]
    public void OldFriendFormatAndWrongCharactersAreRefused(string? input)
        => Assert.False(Code.TryParse(input, out _));

    [Fact]
    public void CodeHashIsAStableBase64UrlHmacOfTheTwelveCharacters()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Assert.True(Code.TryParse("abcd-efgh-jkmn", out var code));
        Assert.Equal("2lIOgCACwQq00MxXy5ecI1JwDNMvfvn7ZTxhVWStYUM", code.Hash(key));
        Assert.True(Code.HashesEqual(code.Hash(key), "2lIOgCACwQq00MxXy5ecI1JwDNMvfvn7ZTxhVWStYUM"));
        Assert.False(Code.HashesEqual(code.Hash(key), "AmnW9EeCsmpV1MD5oaKjfVkShbEGhyu0QpemYo4npV8")); // ABCD-EFGH-JKMP
    }
}
