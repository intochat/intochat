using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
namespace DigitalBrain.Modules.Google.Gmail.Tests;

public sealed class GmailPushAuthenticationFacts
{
    [Fact]
    public void UnsignedProviderTokenCannotAuthenticate()
    {
        var token = new JwtSecurityToken("https://accounts.google.com", "audience", expires: DateTime.UtcNow.AddMinutes(5));
        Assert.ThrowsAny<SecurityTokenException>(() => GmailPushAuthenticator.ValidatePushToken(new JwtSecurityTokenHandler().WriteToken(token), new OpenIdConnectConfiguration(), "audience", "service@project.iam.gserviceaccount.com"));
    }
    [Fact]
    public async Task SecuredHostLetsOnlyVerifiedPushThroughTheModuleGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<GmailModule>()
            .WithExecution(new() { PrivateConfiguration = new Dictionary<string, string?>() { [AuthOptions.PostureKey] = "Secured" } })
            .ConfigureSilo(silo => silo.Services.AddSingleton<IGmailPushAuthenticator>(new TestPushAuthenticator()))
            .WithHttpEdge().StartAsync(ct);
        using var rejected = await brain.HttpClient.PostAsJsonAsync("/google/gmail/watch/owner", new { message = new { data = "e30=" } }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        brain.HttpClient.DefaultRequestHeaders.Authorization = new("Bearer", "test-google-push");
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"emailAddress\":\"unconnected@gmail.com\",\"historyId\":\"1\"}"));
        using var accepted = await brain.HttpClient.PostAsJsonAsync("/google/gmail/watch/owner", new { message = new { data = payload } }, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }
}
