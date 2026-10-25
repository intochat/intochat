using System.Text.RegularExpressions;

namespace IntoChat.OS.Modules.Circles.Tests.Unit;

public sealed class InviteCodeFacts
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void AGeneratedCodeIsTwelveCrockfordCharactersInThreeGroups()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"), InviteCode.Generate());
        }
        Assert.NotEqual(InviteCode.Generate(), InviteCode.Generate());
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn", "ABCD-EFGH-JKMN")]
    [InlineData("  ABCDEFGHJKMN ", "ABCD-EFGH-JKMN")]
    [InlineData("ilo0-1111-2222", "1100-1111-2222")]
    public void NormalizingAcceptsCaseDashesSpacesAndLookalikes(string input, string expected)
    {
        Assert.True(InviteCode.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKM")]
    [InlineData("ABCD-EFGH-JKMNP")]
    [InlineData("ABCD-EFGH-JKMU")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizingRejectsWrongLengthAndU(string? input) => Assert.False(InviteCode.TryNormalize(input, out _));

    [Fact]
    public void EqualCodesHashEqualAndTheHashIsNotTheCode()
    {
        var hash = InviteCode.Hash("ABCD-EFGH-JKMN", Key);
        Assert.Equal(hash, InviteCode.Hash("abcdefghjkmn", Key));
        Assert.True(InviteCode.HashesEqual(hash, InviteCode.Hash(" abcd-efgh-jkmn ", Key)));
        Assert.False(InviteCode.HashesEqual(hash, InviteCode.Hash("ABCD-EFGH-JKMP", Key)));
        Assert.NotEqual(hash, InviteCode.Hash("ABCD-EFGH-JKMN", new byte[32]));
        Assert.DoesNotContain("ABCD", hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(43, hash.Length);
    }

    [Fact]
    public void ValidityIsClampedBetweenFifteenMinutesAndSevenDays()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), InviteCode.Clamp(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromDays(7), InviteCode.Clamp(TimeSpan.FromDays(30)));
        Assert.Equal(TimeSpan.FromHours(2), InviteCode.Clamp(TimeSpan.FromHours(2)));
    }
}
