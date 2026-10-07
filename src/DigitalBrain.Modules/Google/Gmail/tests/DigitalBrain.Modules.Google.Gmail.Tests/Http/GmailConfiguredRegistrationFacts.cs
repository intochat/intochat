using DigitalBrain.Framework.Platform.Connections;
using System.Net;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Google.Gmail;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

[Collection(GmailHostCollection.Name)]
public sealed class GmailConfiguredRegistrationFacts(GmailHostFixture host)
{
    [Fact]
    public async Task AuthorizationCodeCallbackPublishesGmailConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var principal = "owner";
        var key = ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "gmail", "primary");
        var gmail = brain.Get<IGmail>(key);
        var state = brain.SiloServices.GetRequiredService<GmailCallbackRequests>().Begin(principal, "owner", "owner", "primary");
        await using var connected = await brain.Observe<GmailConnected>(gmail, ct);
        using var response = await brain.HttpClient.GetAsync($"google/gmail/oauth/callback?code=fake-code&state={state}", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user@gmail.com", (await connected.NextAsync(ct: ct)).EmailAddress);
    }

    [Fact]
    public async Task CallbackRejectsMissingStateAndDoesNotReuseClaimedState()
    {
        var ct = TestContext.Current.CancellationToken;
        using var missing = await host.Brain.HttpClient.GetAsync("google/gmail/oauth/callback?code=fake-code", ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", missing.Headers.GetValues("Referrer-Policy").Single());
        using var retired = await host.Brain.HttpClient.GetAsync("/integrations/gmail/login?request=old", ct);
        Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);
        var requests = host.Brain.SiloServices.GetRequiredService<GmailCallbackRequests>();
        var state = requests.Begin("owner", "owner", "owner", "single-use");
        Assert.False(requests.TryClaim(state, "other", "owner", out _));
        Assert.False(requests.TryClaim(state, "owner", "other-account", out _));
        Assert.True(requests.TryClaim(state, "owner", "owner", out var target));
        Assert.Equal("owner", target.AccountId);
        Assert.Equal("single-use", target.ConnectionId);
        Assert.False(requests.TryClaim(state, "owner", "owner", out _));
    }
}
