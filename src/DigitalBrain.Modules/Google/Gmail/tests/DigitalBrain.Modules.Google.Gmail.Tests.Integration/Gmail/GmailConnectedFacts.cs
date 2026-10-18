using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Connections.Signals;
using DigitalBrain.Google.Gmail;
using Microsoft.AspNetCore.Http;

namespace DigitalBrain.Modules.Google.Gmail.Tests.Integration;

// Each fact flips its own Google stub, so each starts its own brain.
public sealed class GmailConnectedFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AGrantConnectsTheMailboxAndTracksTheConnectionInItsBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stub = await TokenEndpointStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(stub, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "primary");
        await using var connected = await brain.Observe<ConnectionConnected>(brain.Get<IGmail>(key), ct);
        using (brain.Owner.AsCaller())
        {
            var view = await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), GmailScopes.Read, null, "{}"));
            Assert.Equal("user@gmail.com", view.Account);
        }
        Assert.Equal("user@gmail.com", (await connected.NextAsync(ct: ct)).Account);
        Assert.Equal([key], await brain.Grains().GetGrain<IConnectionIndex>(brain.Owner.BrainId).List());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnExpiredAccessTokenIsRefreshedThroughThePlatformBeforeAPush()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stub = await TokenEndpointStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(stub, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "primary");
        using (brain.Owner.AsCaller())
        { await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "stale-token", "refresh-token", DateTimeOffset.UtcNow.AddSeconds(-1), GmailScopes.Read, null, "{}")); }
        await using var mail = await brain.Observe<MailReceived>(brain.Get<IGmail>(key), ct);
        // A refreshed token living under the refresh lead makes the second push refresh again.
        stub.RefreshExpiresIn = 10;
        var inbound = brain.Grains().GetGrain<IGmailInbound>(key);
        await inbound.AcceptWatchPush(new("h1", "user@gmail.com"));
        Assert.Equal("h1", (await mail.NextAsync(ct: ct)).HistoryId);
        await inbound.AcceptWatchPush(new("h2", "user@gmail.com"));
        Assert.Equal("h2", (await mail.NextAsync(ct: ct)).HistoryId);
        // The refresh answer carries no refresh token, as Google's does; the second refresh still had one.
        Assert.Equal(2, stub.Refreshes);
        Assert.Equal("refresh_token", stub.LastForm!["grant_type"]);
        Assert.Equal("refresh-token", stub.LastForm["refresh_token"]);
        using (brain.Owner.AsCaller())
        { Assert.True((await brain.Get<IConnection>(key).Read()).ExpiresAt > DateTimeOffset.UtcNow); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APushRefreshesAConnectionAProbeFoundExpiredAndReconnectsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stub = await TokenEndpointStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(stub, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "primary");
        using (brain.Owner.AsCaller())
        {
            await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "stale-token", "refresh-token", DateTimeOffset.UtcNow.AddSeconds(-1), GmailScopes.Read, null, "{}"));
            stub.ProfileStatus = StatusCodes.Status401Unauthorized;
            Assert.Equal(ConnectionStatus.Expired, (await brain.Get<IConnection>(key).Probe()).Status);
        }
        await using var mail = await brain.Observe<MailReceived>(brain.Get<IGmail>(key), ct);
        await brain.Grains().GetGrain<IGmailInbound>(key).AcceptWatchPush(new("after-probe", "user@gmail.com"));
        Assert.Equal("after-probe", (await mail.NextAsync(ct: ct)).HistoryId);
        Assert.Equal(1, stub.Refreshes);
        using (brain.Owner.AsCaller())
        { Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(key).Read()).Status); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProbeAsksGoogleWithTheStoredTokenAndMapsItsAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stub = await TokenEndpointStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(stub, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "primary");
        using var caller = brain.Owner.AsCaller();
        await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "probe-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), GmailScopes.Read, null, "{}"));
        var connection = brain.Get<IConnection>(key);

        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        Assert.Equal("Bearer probe-token", stub.LastProfileAuthorization);

        stub.ProfileStatus = StatusCodes.Status401Unauthorized;
        var refused = await connection.Probe();
        Assert.Equal(ConnectionStatus.Expired, refused.Status);
        Assert.Equal("The Gmail access token was refused.", refused.Detail);

        stub.ProfileStatus = StatusCodes.Status503ServiceUnavailable;
        var failing = await connection.Probe();
        Assert.Equal(ConnectionStatus.Failing, failing.Status);
        Assert.Contains("503", failing.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-token", failing.Detail, StringComparison.Ordinal);

        stub.ProfileStatus = StatusCodes.Status200OK;
        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
    }
}
