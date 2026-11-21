using IntoChat.OS.Modules.Circles;
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
    public void CodeHashesExactlyLikeTheOldInviteCode()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Assert.True(Code.TryParse("abcd-efgh-jkmn", out var code));
        Assert.Equal(InviteCode.Hash("ABCD-EFGH-JKMN", key), code.Hash(key));
        Assert.True(Code.HashesEqual(code.Hash(key), InviteCode.Hash("ABCD-EFGH-JKMN", key)));
        Assert.False(Code.HashesEqual(code.Hash(key), InviteCode.Hash("ABCD-EFGH-JKMP", key)));
    }
}
