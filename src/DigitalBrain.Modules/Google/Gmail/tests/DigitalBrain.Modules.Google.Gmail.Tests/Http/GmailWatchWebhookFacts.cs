using System.Net;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Google.Gmail;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

[Collection(GmailHostCollection.Name)]
public sealed class GmailWatchWebhookFacts(GmailHost host)
{
    [Fact]
    public async Task GmailWatchHttpPublishesMailReceived()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await brain.PersonAsync("push", ct);
        await person.ConnectAsync("push", ct);
        await using var mail = await brain.Observe<MailReceived>(brain.Get<IGmail>(ConnectionKeys.For(person.BrainId, "gmail", "push")), ct);
        using var response = await brain.PushAsync("user@gmail.com", "123", ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var received = await mail.NextAsync(ct: ct);
        Assert.Equal("user@gmail.com", received.EmailAddress);
        Assert.Equal("123", received.HistoryId);
    }

    [Fact]
    public async Task UserDeliveryCannotProbeAnotherBrainsConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var member = await brain.PersonAsync("member", ct);
        var outsider = await brain.PersonAsync("outsider", ct);
        await member.ConnectAsync("scoped-push", ct);
        var key = ConnectionKeys.For(member.BrainId, "gmail", "scoped-push");
        var inbound = brain.Grains().GetGrain<IGmailInbound>(key);
        using (outsider.AsCaller())
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptWatchPush(new("denied", "user@gmail.com"))); }
        await using var mail = await brain.Observe<MailReceived>(brain.Get<IGmail>(key), ct);
        using (member.AsCaller()) { await inbound.AcceptWatchPush(new("allowed", "user@gmail.com")); }
        Assert.Equal("allowed", (await mail.NextAsync(ct: ct)).HistoryId);
    }

    [Fact]
    public async Task OneMailboxFansOutToConnectionsInTwoBrainsAndOtherMailboxesStaySeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var firstPerson = await brain.PersonAsync("first", ct);
        var secondPerson = await brain.PersonAsync("second", ct);
        var first = ConnectionKeys.For(firstPerson.BrainId, "gmail", "shared");
        var second = ConnectionKeys.For(secondPerson.BrainId, "gmail", "shared");
        await firstPerson.ConnectAsync("shared", ct);
        await secondPerson.ConnectAsync("shared", ct);
        var index = brain.Grains().GetGrain<IGmailMailboxIndex>("user@gmail.com");
        await index.Add(first);
        Assert.Equal(1, (await index.Read()).Count(x => x == first));
        Assert.Contains(second, await index.Read());
        Assert.Empty(await brain.Grains().GetGrain<IGmailMailboxIndex>("other@gmail.com").Read());
        await using var firstMail = await brain.Observe<MailReceived>(brain.Get<IGmail>(first), ct);
        await using var secondMail = await brain.Observe<MailReceived>(brain.Get<IGmail>(second), ct);
        using var response = await brain.PushAsync("user@gmail.com", "fanout", ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("fanout", (await firstMail.NextAsync(ct: ct)).HistoryId);
        Assert.Equal("fanout", (await secondMail.NextAsync(ct: ct)).HistoryId);
    }

    [Fact]
    public async Task ARevokedRefreshExpiresItsConnectionAndTheFanOutStillReachesTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var lapsed = await brain.PersonAsync("lapsed", ct);
        var live = await brain.PersonAsync("live", ct);
        var revoked = ConnectionKeys.For(lapsed.BrainId, "gmail", "lapsed");
        var healthy = ConnectionKeys.For(live.BrainId, "gmail", "live");
        // The revoked connection is indexed first, so the loop meets its failure before the healthy one.
        using (lapsed.AsCaller())
        { await brain.Grains().GetConnectionInbound(revoked).AcceptGrant(new("google-oauth", "stale-token", TokenEndpointStub.RevokedRefreshToken, DateTimeOffset.UtcNow.AddSeconds(-1), GmailScopes.Read, null, "{}")); }
        await live.ConnectAsync("live", ct);
        var index = await brain.Grains().GetGrain<IGmailMailboxIndex>("user@gmail.com").Read();
        Assert.True(Array.IndexOf(index, revoked) < Array.IndexOf(index, healthy));
        await using var healthyMail = await brain.Observe<MailReceived>(brain.Get<IGmail>(healthy), ct);
        using var response = await brain.PushAsync("user@gmail.com", "after-revoke", ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("after-revoke", (await healthyMail.NextAsync(ct: ct)).HistoryId);
        using (lapsed.AsCaller())
        {
            var view = await brain.Get<IConnection>(revoked).Read();
            Assert.Equal(ConnectionStatus.Expired, view.Status);
            Assert.DoesNotContain(TokenEndpointStub.RevokedRefreshToken, view.Detail ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WrongMailboxAndDisconnectedConnectionsPublishNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await brain.PersonAsync("restricted", ct);
        await person.ConnectAsync("restricted-push", ct);
        var key = ConnectionKeys.For(person.BrainId, "gmail", "restricted-push");
        var inbound = brain.Grains().GetGrain<IGmailInbound>(key);
        await using var mail = await brain.Observe<MailReceived>(brain.Get<IGmail>(key), ct);
        await inbound.AcceptWatchPush(new("wrong", "other@gmail.com"));
        using (person.AsCaller()) { await brain.Get<IConnection>(key).Disconnect(); }
        await inbound.AcceptWatchPush(new("disconnected", "user@gmail.com"));
        // Reconnecting reopens delivery: the first signal the probe sees is the one after it.
        await person.ConnectAsync("restricted-push", ct);
        await inbound.AcceptWatchPush(new("reconnected", "user@gmail.com"));
        Assert.Equal("reconnected", (await mail.NextAsync(ct: ct)).HistoryId);
    }
}
