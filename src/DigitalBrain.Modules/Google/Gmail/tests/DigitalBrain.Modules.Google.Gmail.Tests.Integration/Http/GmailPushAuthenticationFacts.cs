using System.IdentityModel.Tokens.Jwt;
using System.Net;
using DigitalBrain.Google.Gmail;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DigitalBrain.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailPushAuthenticationFacts
{
    [Fact]
    public void UnsignedProviderTokenCannotAuthenticate()
    {
        var token = new JwtSecurityToken("https://accounts.google.com", "audience", expires: DateTime.UtcNow.AddMinutes(5));
        Assert.ThrowsAny<SecurityTokenException>(() => GmailPushAuthenticator.ValidatePushToken(new JwtSecurityTokenHandler().WriteToken(token), new OpenIdConnectConfiguration(), "audience", "service@project.iam.gserviceaccount.com"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SecuredHostLetsOnlyVerifiedPushThroughTheModuleGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stub = await TokenEndpointStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(stub, ct, posture: "Secured");
        using var rejected = await brain.PushAsync("unregistered", "unconnected@gmail.com", "1", ct, bearer: "unverified");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var accepted = await brain.PushAsync("unregistered", "unconnected@gmail.com", "1", ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }
}
