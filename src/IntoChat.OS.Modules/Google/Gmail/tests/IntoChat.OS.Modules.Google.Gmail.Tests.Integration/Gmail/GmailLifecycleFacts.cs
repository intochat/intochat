using System.Net;
using System.Text;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Google.Gmail.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailLifecycleFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task RevokedWatchMarksConnectionExpiredAndStopsAutomaticRenewal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google, watch: true)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        await brain.Owner.ConnectAsync("revoked-watch", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "revoked-watch");
        var gmail = brain.Get<IGmail>(key);
        google.WatchReplies.Enqueue(new(401));
        clock.Advance(TimeSpan.FromDays(1));
        await gmail.AsReference<IRemindable>().ReceiveReminder("gmail-lifecycle", default, ct);
        using (brain.Owner.AsCaller())
        {
            var view = await brain.Get<IConnection>(key).Read();
            Assert.Equal(ConnectionStatus.Expired, view.Status);
            Assert.Contains("Reconnect", view.Detail, StringComparison.Ordinal);
        }
        clock.Advance(TimeSpan.FromDays(1));
        await gmail.AsReference<IRemindable>().ReceiveReminder("gmail-lifecycle", default, ct);
        Assert.Equal(2, google.Requests.Count(request => request.Path.EndsWith("/watch", StringComparison.Ordinal)));
        Assert.Empty((await brain.Services.GetRequiredService<IReminderTable>().ReadRows(gmail.GetGrainId(), ct)).Reminders);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task MissingWatchConfigurationIsVisibleWithoutDisablingReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        await brain.Owner.ConnectAsync("unconfigured", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "unconfigured");
        using var caller = brain.Owner.AsCaller();
        var view = await brain.Get<IConnection>(key).Read();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Contains("disabled", view.Detail, StringComparison.Ordinal);
        Assert.NotNull((await brain.Get<IGmail>(key).ReadInbox(new(10, null), ct)).Value);
        Assert.DoesNotContain(google.Requests, request => request.Path.EndsWith("/watch", StringComparison.Ordinal));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task FailedWatchRegistrationKeepsConnectionAndSuccessfulRetryClearsWatchDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        google.WatchReplies.Enqueue(new(503));
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google, watch: true)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        await brain.Owner.ConnectAsync("retry-watch", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "retry-watch");
        using (brain.Owner.AsCaller())
        {
            var failed = await brain.Get<IConnection>(key).Read();
            Assert.Equal(ConnectionStatus.Connected, failed.Status);
            Assert.Contains("retry", failed.Detail, StringComparison.Ordinal);
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        await brain.Get<IGmail>(key).AsReference<IRemindable>().ReceiveReminder("gmail-lifecycle", default, ct);
        using (brain.Owner.AsCaller()) { Assert.Null((await brain.Get<IConnection>(key).Read()).Detail); }
        Assert.Equal(2, google.Requests.Count(request => request.Path.EndsWith("/watch", StringComparison.Ordinal)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task HistoryCommitsProviderCursorAndDuplicatesMakeNoProviderRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        await brain.Owner.ConnectAsync("history", ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "history");
        await using var changes = await brain.Observe<MailReceived>(brain.Get<IGmail>(key), ct);
        google.HistoryReplies.Enqueue(new(200, new { historyId = "120", history = new[] { new { id = "105" } } }));
        using var response = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "999", ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("120", (await changes.NextAsync(ct: ct)).HistoryId);
        Assert.Equal("100", Assert.Single(google.Requests, request => request.Path.EndsWith("/history", StringComparison.Ordinal)).Query["startHistoryId"]);
        google.ClearRequests();
        using var duplicate = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "120", ct);
        using var older = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "9", ct);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        Assert.Empty(google.Requests);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TenPageBoundSurvivesReactivationAndKeepsOriginalStartCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        await brain.Owner.ConnectAsync("paged", ct);
        var gmail = brain.Get<IGmail>(ConnectionKeys.For(brain.Owner.BrainId, "gmail", "paged"));
        for (var page = 1; page <= 10; page++)
        { google.HistoryReplies.Enqueue(new(200, new { historyId = "150", nextPageToken = $"page-{page}", history = Array.Empty<object>() })); }
        google.HistoryReplies.Enqueue(new(200, new { historyId = "150", history = Array.Empty<object>() }));
        using var first = await brain.PushAsync(brain.Owner.BrainId, google.EmailAddress, "140", ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(10, google.Requests.Count(request => request.Path.EndsWith("/history", StringComparison.Ordinal)));
        await brain.DeactivateAsync(gmail, ct);
        await using var changes = await brain.Observe<MailReceived>(gmail, ct);
        await gmail.AsReference<IRemindable>().ReceiveReminder("gmail-lifecycle", default, ct);
        Assert.Equal("150", (await changes.NextAsync(ct: ct)).HistoryId);
        var pages = google.Requests.Where(request => request.Path.EndsWith("/history", StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, pages.Length);
        Assert.All(pages, request => Assert.Equal("100", request.Query["startHistoryId"]));
        Assert.Equal("page-10", pages[^1].Query["pageToken"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WatchRenewsWithModuleClockAndDisconnectOnlyReleasesLocalWatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google, watch: true)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        await brain.Owner.ConnectAsync("watch", ct);
        var gmail = brain.Get<IGmail>(ConnectionKeys.For(brain.Owner.BrainId, "gmail", "watch"));
        Assert.Single(google.Requests, request => request.Path.EndsWith("/watch", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromDays(1));
        await gmail.AsReference<IRemindable>().ReceiveReminder("gmail-lifecycle", default, ct);
        Assert.Equal(2, google.Requests.Count(request => request.Path.EndsWith("/watch", StringComparison.Ordinal)));
        using (brain.Owner.AsCaller()) { await gmail.AsReference<IConnection>().Disconnect(); }
        await gmail.AsReference<IRemindable>().ReceiveReminder("gmail-lifecycle", default, ct);
        Assert.DoesNotContain(google.Requests, request => request.Path.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Empty((await brain.Services.GetRequiredService<IReminderTable>().ReadRows(gmail.GetGrainId(), ct)).Reminders);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AuthenticationPrecedesBodyParsingAndValidBodiesAreBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        using var unauthorized = new HttpRequestMessage(HttpMethod.Post, GmailModule.WatchPath + "/" + brain.Owner.BrainId)
        { Content = new StringContent("not json", Encoding.UTF8, "application/json") };
        using var rejected = await brain.HttpClient.SendAsync(unauthorized, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, GmailModule.WatchPath + "/" + brain.Owner.BrainId)
        { Content = JsonContent.Create(new { padding = new string('a', 17000) }) };
        oversized.Headers.Authorization = new("Bearer", GmailBrain.PushToken);
        using var tooBig = await brain.HttpClient.SendAsync(oversized, ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Empty(google.Requests);
    }
}
