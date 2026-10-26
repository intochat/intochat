using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Google.Gmail.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailHistoryRecoveryFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task VerifiedCursorBehindPushTargetRetainsWorkAndDoesNotRepublishUnchangedCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        await brain.Owner.ConnectAsync("behind", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "behind");
        google.HistoryId = "120";
        using var first = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "150", ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var pending = Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!;
        Assert.Equal("120", pending.Cursor);
        Assert.Equal("120", pending.Work!.StartHistoryId);
        Assert.Equal("150", pending.Work.TargetHistoryId);
        clock.Advance(TimeSpan.FromMinutes(1));
        await brain.RemindGmail(key, ct);
        Assert.Single(faults.JournaledMail);
        google.HistoryId = "160";
        clock.Advance(TimeSpan.FromMinutes(1));
        await brain.RemindGmail(key, ct);
        Assert.Equal(2, faults.JournaledMail.Length);
        Assert.Equal("160", faults.JournaledMail[^1].HistoryId);
        Assert.Null(Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!.Work);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RedeliveryCoalescesTargetWithoutBypassingProviderRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        await brain.Owner.ConnectAsync("cooldown", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "cooldown");
        google.HistoryReplies.Enqueue(new(429, Headers: new Dictionary<string, string> { ["Retry-After"] = "3600" }));
        using var first = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        var retryAt = Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!.RetryAt;
        google.ClearRequests();
        using var again = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "130", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, again.StatusCode);
        Assert.Empty(google.Requests);
        var pending = Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!;
        Assert.Equal(retryAt, pending.RetryAt);
        Assert.Equal("130", pending.Work!.TargetHistoryId);
        google.HistoryId = "130";
        clock.Advance(TimeSpan.FromHours(1));
        await brain.RemindGmail(key, ct);
        Assert.Equal("130", Assert.Single(faults.JournaledMail).HistoryId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task FailedLaterPageResumesItsTokenAndEveryAttemptIsVisibleUnderOriginalTrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        await brain.Owner.ConnectAsync("retry", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "retry");
        google.ClearRequests();
        google.HistoryReplies.Enqueue(new(200, new { historyId = "120", nextPageToken = "next-page", history = Array.Empty<object>() }));
        for (var attempt = 0; attempt < 3; attempt++)
        { google.HistoryReplies.Enqueue(new(503, Headers: attempt == 2 ? new Dictionary<string, string> { ["Retry-After"] = "0" } : null)); }
        using var failed = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var pending = Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!;
        Assert.Equal("100", pending.Cursor);
        Assert.Equal("next-page", pending.Work!.PageToken);
        await brain.DeactivateAsync(brain.Get<IGmail>(key), ct);
        google.HistoryId = "130";
        using var retry = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "130", ct);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var signal = Assert.Single(faults.JournaledMail);
        Assert.Equal(pending.Work.Trace.TraceId, signal.Trace!.TraceId);
        var pages = google.Requests.Where(request => request.Path.EndsWith("/history", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, pages.Length);
        Assert.All(pages.Skip(1), request => Assert.Equal("next-page", request.Query["pageToken"]));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var usage = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>($"/brains/{brain.Owner.BrainId}/compute/traces/{signal.Trace.TraceId}", json, ct))!;
        var rows = usage.Rows.Where(row => row.Item == GoogleOperations.History).ToArray();
        Assert.Equal(5, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(key, row.Neuron);
            Assert.Equal(pending.Work.InvocationId, row.InvocationId);
            Assert.Equal(FundingKind.OwnConnection, row.FundingKind);
            Assert.Equal(PricingStatus.Unpriced, row.PricingStatus);
        });
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedHistoryWriteDoesNotAdvanceTheSameActivation(bool commit)
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        await brain.Owner.ConnectAsync("history", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "history");
        google.HistoryId = "120";
        if (commit) { faults.FailNextHistoryCommit = true; } else { faults.FailNextHistoryWork = true; }
        using var failed = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("100", Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!.Cursor);
        Assert.Empty(faults.JournaledMail);
        Assert.Equal(commit ? 1 : 0, google.Requests.Count(request => request.Path.EndsWith("/history", StringComparison.Ordinal)));
        using var retry = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal("120", Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!.Cursor);
        Assert.Single(faults.JournaledMail);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData("append", 1)]
    [InlineData("acknowledgement", 2)]
    [InlineData("clear", 2)]
    public async Task PersistedWakeupSurvivesJournalFailureAndReactivation(string failure, int appendCount)
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        await brain.Owner.ConnectAsync("outbox", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "outbox");
        var gmail = brain.Get<IGmail>(key);
        google.HistoryId = "120";
        faults.FailNextMailAppend = failure == "append";
        faults.LoseNextMailAppendAcknowledgement = failure == "acknowledgement";
        faults.FailNextWakeupClear = failure == "clear";
        using var response = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var pending = Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!;
        Assert.Equal("120", pending.Cursor);
        Assert.NotNull(pending.Wakeup);
        Assert.Null(pending.Work);
        await brain.DeactivateAsync(gmail, ct);
        await brain.RemindGmail(key, ct);
        Assert.Null(Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!.Wakeup);
        Assert.Equal(appendCount, faults.JournaledMail.Length);
        Assert.All(faults.JournaledMail, signal =>
        {
            Assert.Equal("120", signal.HistoryId);
            Assert.Equal(pending.Wakeup.Trace.TraceId, signal.Trace!.TraceId);
            Assert.Equal(brain.Owner.BrainId, signal.Trace.BrainId);
        });
        Assert.Single(google.Requests, request => request.Path.EndsWith("/history", StringComparison.Ordinal));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnExpiredHistoryCursorResyncsOneProfileWithoutScanningInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        await brain.Owner.ConnectAsync("expired", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "expired");
        google.ClearRequests();
        google.HistoryId = "200";
        google.HistoryReplies.Enqueue(new(404));
        using var response = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "150", ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var history = Assert.IsType<GmailState>(faults.SnapshotFor(key)).History!;
        Assert.Equal("200", history.Cursor);
        Assert.True(history.ResyncRequired);
        Assert.Equal("200", Assert.Single(faults.JournaledMail).HistoryId);
        Assert.Single(google.Requests, request => request.Path.EndsWith("/profile", StringComparison.Ordinal));
        Assert.Single(google.Requests, request => request.Path.EndsWith("/history", StringComparison.Ordinal));
        Assert.DoesNotContain(google.Requests, request => request.Path.Contains("/messages", StringComparison.Ordinal));
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshKeepsOmittedScopesButHonorsAnExplicitNarrowedGrant(bool omitScope)
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "refresh");
        var original = GmailScopes.Read + " https://www.googleapis.com/auth/gmail.compose";
        using (brain.Owner.AsCaller())
        { await brain.Plain().GetConnectionInbound(key).AcceptGrant(new("google-oauth-compose", "access", "refresh", DateTimeOffset.UtcNow.AddSeconds(-1), original, null, "{}")); }
        google.Scope = GmailScopes.Read;
        google.OmitRefreshScope = omitScope;
        google.HistoryId = "120";
        using var response = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(omitScope ? original : GmailScopes.Read, Assert.IsType<GmailState>(faults.SnapshotFor(key)).Scopes);
        Assert.Equal(1, google.Refreshes);
        Assert.Equal("refresh", google.LastForm!["refresh_token"]);
    }
}
