using DigitalBrain.Testing.Module;
using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Google.Gmail;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

[Collection(GmailHostCollection.Name)]
public sealed class GmailWatchWebhookFacts(GmailHostFixture host)
{
    [Fact]
    public async Task UserDeliveryCannotProbeAnotherBrainsConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await host.ConnectForPushAsync("member", "scoped-push");
        var key = ConnectionKeys.For(BrainScope.Create("owner", "member").Id, "gmail", "scoped-push");
        var inbound = brain.Grains.GetGrain<IGmailInbound>(key);
        var caller = new DigitalBrain.Framework.CallerContext
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "other",
            Kind = DigitalBrain.Framework.CallerKind.User,
            StampedBy = DigitalBrain.Framework.TrustedEdge.AuthenticatedHttp,
        };
        CallerContextStamper.Stamp(caller);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptWatchPush(new("denied", "user@gmail.com")));
            CallerContextStamper.Stamp(caller with { BrainId = "member" });
            await using var mail = await brain.Observe<MailReceived>(brain.Get<IGmail>(key), ct);
            await inbound.AcceptWatchPush(new("allowed", "user@gmail.com"));
            Assert.Equal("allowed", (await mail.NextAsync(ct: ct)).HistoryId);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task GmailWatchHttpPublishesMailReceived()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        brain.HttpClient.DefaultRequestHeaders.Authorization = new("Bearer", "test-google-push");
        var key = ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "gmail", "push");
        await host.ConnectForPushAsync("owner", "push");
        var gmail = brain.Get<IGmail>(key);
        await brain.Grains.GetGrain<IGmailMailboxIndex>("user@gmail.com").Add(key);
        await using var mail = await brain.Observe<MailReceived>(gmail, ct);
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { emailAddress = "user@gmail.com", historyId = "123" })));
        using var response = await brain.HttpClient.PostAsJsonAsync(
            "/google/gmail/watch",
            new { message = new { data, messageId = "m1" }, subscription = "projects/x/subscriptions/gmail" },
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("123", (await mail.NextAsync(ct: ct)).HistoryId);
    }

    [Fact]
    public async Task OneMailboxFansOutToConnectionsInTwoBrainsAndOtherMailboxesStaySeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        brain.HttpClient.DefaultRequestHeaders.Authorization = new("Bearer", "test-google-push");
        var first = ConnectionKeys.For(BrainScope.Create("owner", "first").Id, "gmail", "shared");
        var second = ConnectionKeys.For(BrainScope.Create("owner", "second").Id, "gmail", "shared");
        await host.ConnectForPushAsync("first", "shared");
        await host.ConnectForPushAsync("second", "shared");
        var index = brain.Grains.GetGrain<IGmailMailboxIndex>("user@gmail.com");
        await index.Add(first);
        await index.Add(second);
        await index.Add(first);
        Assert.Equal(1, (await index.Read()).Count(x => x == first));
        Assert.Contains(second, await index.Read());
        Assert.Empty(await brain.Grains.GetGrain<IGmailMailboxIndex>("other@gmail.com").Read());
        await using var firstMail = await brain.Observe<MailReceived>(brain.Get<IGmail>(first), ct);
        await using var secondMail = await brain.Observe<MailReceived>(brain.Get<IGmail>(second), ct);
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { emailAddress = "user@gmail.com", historyId = "fanout" })));
        using var response = await brain.HttpClient.PostAsJsonAsync("/google/gmail/watch", new { message = new { data, messageId = "fanout" } }, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("fanout", (await firstMail.NextAsync(ct: ct)).HistoryId);
        Assert.Equal("fanout", (await secondMail.NextAsync(ct: ct)).HistoryId);
    }

    [Fact]
    public async Task ARevokedRefreshExpiresItsConnectionAndTheFanOutStillReachesTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        brain.HttpClient.DefaultRequestHeaders.Authorization = new("Bearer", "test-google-push");
        var revoked = ConnectionKeys.For(BrainScope.Create("owner", "revoked").Id, "gmail", "lapsed");
        var healthy = ConnectionKeys.For(BrainScope.Create("owner", "healthy").Id, "gmail", "live");
        // The revoked connection is indexed first, so the loop meets its failure before the healthy one.
        CallerContextStamper.Stamp(new DigitalBrain.Framework.CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "revoked", Kind = DigitalBrain.Framework.CallerKind.User, StampedBy = DigitalBrain.Framework.TrustedEdge.AuthenticatedHttp });
        try
        {
            await brain.Grains.GetConnectionInbound(revoked).AcceptGrant(new("google-oauth", "stale-token", TokenEndpointStub.RevokedRefreshToken, DateTimeOffset.UtcNow.AddSeconds(-1), GmailScopes.Read, null, "{}"));
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
        await host.ConnectForPushAsync("healthy", "live");
        var index = await brain.Grains.GetGrain<IGmailMailboxIndex>("user@gmail.com").Read();
        Assert.True(Array.IndexOf(index, revoked) < Array.IndexOf(index, healthy));
        await using var healthyMail = await brain.Observe<MailReceived>(brain.Get<IGmail>(healthy), ct);
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { emailAddress = "user@gmail.com", historyId = "after-revoke" })));
        using var response = await brain.HttpClient.PostAsJsonAsync("/google/gmail/watch", new { message = new { data, messageId = "after-revoke" } }, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("after-revoke", (await healthyMail.NextAsync(ct: ct)).HistoryId);
        CallerContextStamper.Stamp(new DigitalBrain.Framework.CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "revoked", Kind = DigitalBrain.Framework.CallerKind.User, StampedBy = DigitalBrain.Framework.TrustedEdge.AuthenticatedHttp });
        try
        {
            var view = await brain.Get<IConnection>(revoked).Read();
            Assert.Equal(ConnectionStatus.Expired, view.Status);
            Assert.DoesNotContain(TokenEndpointStub.RevokedRefreshToken, view.Detail ?? "", StringComparison.Ordinal);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task WatchEndpointRejectsUnverifiedDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/google/gmail/watch");
        request.Headers.Authorization = new("Bearer", "unverified");
        request.Content = JsonContent.Create(new { message = new { data = "e30=" } });
        using var response = await host.Brain.HttpClient.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongMailboxAndDisconnectedConnectionsPublishNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await host.ConnectForPushAsync("owner", "restricted-push");
        var key = ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "gmail", "restricted-push");
        var gmail = brain.Get<IGmail>(key);
        await using var mail = await brain.Observe<MailReceived>(gmail, ct);
        await brain.Grains.GetGrain<IGmailInbound>(gmail.GetGrainId()).AcceptWatchPush(new("wrong", "other@gmail.com"));
        CallerContextStamper.Stamp(new DigitalBrain.Framework.CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = DigitalBrain.Framework.CallerKind.User, StampedBy = DigitalBrain.Framework.TrustedEdge.AuthenticatedHttp });
        await brain.Get<IConnection>(key).Disconnect();
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        await brain.Grains.GetGrain<IGmailInbound>(gmail.GetGrainId()).AcceptWatchPush(new("disconnected", "user@gmail.com"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mail.NextAsync(ct: deadline.Token));
    }
}
