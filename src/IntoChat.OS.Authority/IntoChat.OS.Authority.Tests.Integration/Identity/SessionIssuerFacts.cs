using System.Security.Claims;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Authority.Identity;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

public sealed class SessionIssuerFacts
{
    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData(ClaimTypes.Role)]
    [InlineData("os.account")]
    [InlineData("os.brain")]
    public async Task AdditionalClaimsNeverOverridePlatformClaims(string reservedType)
    {
        var member = new Member { PrincipalId = "ada", AccountId = "ada", BrainId = "ada", Role = default, DisplayName = "Ada" };
        var http = new DefaultHttpContext();
        var request = new SessionRequest(member, [new Claim(reservedType, "other")]);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new AccountSessionIssuer().IssueAsync(http, request, TestContext.Current.CancellationToken));

        Assert.Contains(reservedType, error.Message);
    }
}
