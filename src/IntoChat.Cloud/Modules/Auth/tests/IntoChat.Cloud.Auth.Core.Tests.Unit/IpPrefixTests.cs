using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class IpPrefixTests
{
    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("2001:db8:1:2:3::4", "2001:db8:1:2::/64")]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0/24")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("not-an-ip", "unknown")]
    public void Truncate_AppliesPrefixRules(string? input, string expected)
    {
        Assert.Equal(expected, IpPrefix.Truncate(input));
    }
}