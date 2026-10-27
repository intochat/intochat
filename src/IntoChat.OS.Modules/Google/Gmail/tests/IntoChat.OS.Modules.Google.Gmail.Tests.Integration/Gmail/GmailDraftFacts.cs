using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Google.Gmail.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailDraftFacts
{
    private const string Compose = GmailScopes.Read + " https://www.googleapis.com/auth/gmail.compose";
    private static GmailDraftRequest Draft => new(["recipient@example.test"], [], [], "Fixture draft", "Synthetic test body.");

    [Fact, Trait("host", "own")]
    public async Task LocalPreviewAndARepeatedRequestCreateOnlyOneDraftAcrossReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "draft");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        google.ClearRequests();
        var preview = Assert.IsType<GmailPreparedDraft>((await gmail.PreviewDraft(Draft, ct)).Value);
        Assert.Empty(google.Requests);
        var request = new GmailCreateDraftRequest(preview.PreviewId, "stable-request");
        var created = Assert.IsType<GmailDraftOperation>((await gmail.CreateDraft(request, ct)).Value);
        Assert.Equal(GmailDraftOperationStatus.Completed, created.Status);
        Assert.Equal("draft-1", created.DraftId);
        Assert.NotNull(created.Accounting);
        Assert.False(string.IsNullOrWhiteSpace(created.Accounting.TraceId));
        Assert.Equal(created, (await gmail.CreateDraft(request, ct)).Value);
        await brain.DeactivateAsync(gmail, ct);
        Assert.Equal(created, (await gmail.GetDraftOperation(new(request.RequestId), ct)).Value);
        Assert.Equal(created, (await gmail.CreateDraft(request, ct)).Value);
        Assert.Single(google.Requests, value => value.Method == "POST" && value.Path.EndsWith("/drafts", StringComparison.Ordinal));
    }

    [Fact, Trait("host", "own")]
    public async Task AmbiguousDispatchIsRetainedAndNeverAutomaticallyReissued()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "unknown");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        google.ClearRequests();
        google.DraftReplies.Enqueue(new(200, Abort: true));
        var request = new GmailCreateDraftRequest(preview.PreviewId, "uncertain-request");
        var outcome = Assert.IsType<GmailDraftOperation>((await gmail.CreateDraft(request, ct)).Value);
        Assert.Equal(GmailDraftOperationStatus.Unknown, outcome.Status);
        Assert.Equal("outcome_unknown", outcome.Failure?.Code);
        Assert.Null(outcome.RetainUntil);
        await brain.DeactivateAsync(gmail, ct);
        Assert.Equivalent(outcome, (await gmail.GetDraftOperation(new(request.RequestId), ct)).Value, strict: true);
        Assert.Equivalent(outcome, (await gmail.CreateDraft(request, ct)).Value, strict: true);
        Assert.Single(google.Requests, value => value.Path.EndsWith("/drafts", StringComparison.Ordinal));
    }

    [Fact, Trait("host", "own")]
    public async Task ReadOnlyGrantCanPrepareButCannotCreateAndASecondRequestCannotConsumeOnePreview()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "consent");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, GmailScopes.Read);
        var gmail = brain.Get<IGmail>(key);
        var readPreview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        google.ClearRequests();
        Assert.Equal("reconsent_required", (await gmail.CreateDraft(new(readPreview.PreviewId, "needs-compose"), ct)).Failure?.Code);
        Assert.Empty(google.Requests);
        await Grant(brain, key, Compose);
        Assert.Equal("not_found", (await gmail.CreateDraft(new(readPreview.PreviewId, "old-grant"), ct)).Failure?.Code);
        var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        Assert.Equal(GmailDraftOperationStatus.Completed, (await gmail.CreateDraft(new(preview.PreviewId, "first"), ct)).Value?.Status);
        Assert.Equal("request_conflict", (await gmail.CreateDraft(new(preview.PreviewId, "second"), ct)).Failure?.Code);
        var another = (await gmail.PreviewDraft(Draft, ct)).Value!;
        Assert.Equal("request_conflict", (await gmail.CreateDraft(new(another.PreviewId, "first"), ct)).Failure?.Code);
    }

    [Fact, Trait("host", "own")]
    public async Task CreatorIsolationAndOldMailboxReceiptsSurviveReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        using var member = await brain.Owner.AddAccountMemberAsync(brain, "another-fixture-principal");
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "binding");
        var gmail = brain.Get<IGmail>(key);
        GmailPreparedDraft preview;
        using (brain.Owner.AsCaller())
        {
            await Grant(brain, key, Compose);
            preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        }
        using (member.AsCaller())
        { Assert.Equal("not_found", (await gmail.CreateDraft(new(preview.PreviewId, "owner-request"), ct)).Failure?.Code); }
        using (brain.Owner.AsCaller())
        {
            Assert.Equal(GmailDraftOperationStatus.Completed, (await gmail.CreateDraft(new(preview.PreviewId, "owner-request"), ct)).Value?.Status);
            google.EmailAddress = "second@example.test";
            await Grant(brain, key, GmailScopes.Read);
            google.ClearRequests();
            var receipt = (await gmail.GetDraftOperation(new("owner-request"), ct)).Value;
            Assert.Equal("user@gmail.com", receipt?.Mailbox);
            Assert.Equal(receipt, (await gmail.CreateDraft(new(preview.PreviewId, "owner-request"), ct)).Value);
            Assert.Empty(google.Requests);
        }
        using (member.AsCaller())
        { Assert.Equal("not_found", (await gmail.GetDraftOperation(new("owner-request"), ct)).Failure?.Code); }
    }

    [Fact, Trait("host", "own")]
    public async Task PreviewCapacityExpiryAndReceiptRetentionAreExplicit()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "limits");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        Assert.Equal(GmailDraftOperationStatus.Completed, (await gmail.CreateDraft(new(preview.PreviewId, "retained"), ct)).Value?.Status);
        for (var count = 1; count < 100; count++) { Assert.NotNull((await gmail.PreviewDraft(Draft, ct)).Value); }
        Assert.Equal("rate_limited", (await gmail.PreviewDraft(Draft, ct)).Failure?.Code);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.NotNull((await gmail.PreviewDraft(Draft, ct)).Value);
        Assert.Equal(GmailDraftOperationStatus.Completed, (await gmail.GetDraftOperation(new("retained"), ct)).Value?.Status);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal("not_found", (await gmail.GetDraftOperation(new("retained"), ct)).Failure?.Code);
    }

    private static Task<ConnectionView> Grant(ITestBrain brain, string key, string scopes)
        => brain.Plain().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "fixture-access", "fixture-refresh",
            DateTimeOffset.UtcNow.AddDays(7), scopes, null, "{}"));

    [Fact, Trait("host", "own")]
    public async Task FailedIntentPersistenceDispatchesNothingAndDoesNotConsumeThePreview()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "intent-fault");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        google.ClearRequests();
        faults.FailNextDraftIntent = true;
        await Assert.ThrowsAnyAsync<Exception>(() => gmail.CreateDraft(new(preview.PreviewId, "intent-request"), ct));
        Assert.Empty(google.Requests);
        Assert.Equal("not_found", (await gmail.GetDraftOperation(new("intent-request"), ct)).Failure?.Code);
        Assert.Equal(GmailDraftOperationStatus.Completed, (await gmail.CreateDraft(new(preview.PreviewId, "intent-request"), ct)).Value?.Status);
        Assert.Single(google.Requests, request => request.Path.EndsWith("/drafts", StringComparison.Ordinal));
    }

    [Fact, Trait("host", "own")]
    public async Task FailedOutcomePersistenceReturnsUnknownWithKnownIdAndNeverReposts()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "outcome-fault");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        google.ClearRequests();
        faults.FailNextDraftCompletion = true;
        var request = new GmailCreateDraftRequest(preview.PreviewId, "outcome-request");
        var outcome = Assert.IsType<GmailDraftOperation>((await gmail.CreateDraft(request, ct)).Value);
        Assert.Equal(GmailDraftOperationStatus.Unknown, outcome.Status);
        Assert.Equal("draft-1", outcome.DraftId);
        Assert.Equal("outcome_unknown", outcome.Failure?.Code);
        Assert.Equivalent(outcome, (await gmail.CreateDraft(request, ct)).Value, strict: true);
        await brain.DeactivateAsync(gmail, ct);
        Assert.Equivalent(outcome, (await gmail.GetDraftOperation(new(request.RequestId), ct)).Value, strict: true);
        Assert.Equivalent(outcome, (await gmail.CreateDraft(request, ct)).Value, strict: true);
        Assert.Single(google.Requests, value => value.Path.EndsWith("/drafts", StringComparison.Ordinal));
    }

    [Fact, Trait("host", "own")]
    public async Task PendingAccountingPreservesTheKnownDraftAndItsRealUsageCorrelation()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "accounting-fault");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
        faults.FailNextAccountingCompletion = true;
        var created = (await gmail.CreateDraft(new(preview.PreviewId, "pending-accounting"), ct)).Value!;
        Assert.Equal(GmailDraftOperationStatus.Completed, created.Status);
        Assert.Equal("draft-1", created.DraftId);
        Assert.Equal(GmailAccountingStatus.Pending, created.Accounting?.Status);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var usage = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>(
            $"/brains/{brain.Owner.BrainId}/compute/traces/{created.Accounting!.TraceId}", json, ct))!;
        var row = Assert.Single(usage.Rows, value => value.InvocationId == created.Accounting.InvocationId);
        Assert.Equal(GoogleOperations.CreateDraft, row.Item);
        Assert.Equal(key, row.Neuron);
        Assert.Equal(FundingKind.OwnConnection, row.FundingKind);
        Assert.Equal(ExternalCallState.DispatchStarted, row.AttemptState);
        Assert.Equal(1, row.Quantity);
        Assert.Equal("request", row.Unit);
        Assert.Equal(created, (await gmail.GetDraftOperation(new("pending-accounting"), ct)).Value);
    }

    [Fact, Trait("host", "own")]
    public async Task UnresolvedOperationsAreNeverEvictedToAdmitNewDrafts()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google)
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "unresolved-limit");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key, Compose);
        var gmail = brain.Get<IGmail>(key);
        google.ClearRequests();
        for (var index = 0; index < 100; index++)
        {
            var preview = (await gmail.PreviewDraft(Draft, ct)).Value!;
            google.DraftReplies.Enqueue(new(200, new { }));
            var created = (await gmail.CreateDraft(new(preview.PreviewId, $"unknown-{index}"), ct)).Value!;
            Assert.Equal(GmailDraftOperationStatus.Unknown, created.Status);
        }
        clock.Advance(TimeSpan.FromDays(2));
        var next = (await gmail.PreviewDraft(Draft, ct)).Value!;
        var rejected = await gmail.CreateDraft(new(next.PreviewId, "over-limit"), ct);
        Assert.Equal("rate_limited", rejected.Failure?.Code);
        Assert.Null(rejected.Failure?.RetryAt);
        Assert.Equal(GmailDraftOperationStatus.Unknown, (await gmail.GetDraftOperation(new("unknown-0"), ct)).Value?.Status);
        Assert.Equal(100, google.Requests.Count(value => value.Path.EndsWith("/drafts", StringComparison.Ordinal)));
    }
}
