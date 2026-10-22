using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Google.Gmail;
using IntoChat.OS.Google.Gmail.Testing;
using IntoChat.OS.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailToolsFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ComputeJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Trait("host", "own")]
    [Fact]
    public async Task RunningToolsModuleFindsCompleteGmailSchemasAndTracksAvailability()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create().WithModule<ToolsModule>(), google).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "tools");
        using var caller = brain.Owner.AsCaller();
        var gmail = brain.Get<IGmail>(key);
        Assert.Empty((await gmail.Tools(ct)).Tools);
        await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), GmailScopes.Read, null, "{}"));

        google.ClearRequests();
        var descriptors = (await gmail.Tools(ct)).Tools;
        Assert.Equal(["gmail_read_inbox", "gmail_preview_draft", "gmail_create_draft", "gmail_get_draft_operation"], descriptors.Select(tool => tool.Name));
        Assert.Equal([true, false, false, true], descriptors.Select(tool => tool.ReadOnly));
        foreach (var descriptor in descriptors)
        {
            using var schema = JsonDocument.Parse(descriptor.InputSchema);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
        var source = Assert.Single(brain.Services.GetServices<IRuntimeCapabilitySource>().OfType<ToolCapabilitySource>());
        var found = (await source.Read(brain.Owner.BrainId, ct)).Where(type => type.Key == key).ToArray();
        Assert.Equal(4, found.Length);
        Assert.All(found, type => Assert.Contains("Tools()", Assert.Single(type.Methods), StringComparison.Ordinal));
        Assert.Empty(google.Requests);
        google.AddMessage("tool-message", "Tool read", "Body");
        var read = await gmail.Run("gmail_read_inbox", "{\"pageSize\":1}", ct);
        Assert.False(read.IsError);
        var traceId = TraceContext.Current!.TraceId;
        var usage = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>($"/brains/{brain.Owner.BrainId}/compute/traces/{traceId}", ComputeJson, ct))!;
        var rows = usage.Rows.Where(row => row.Item is GoogleOperations.ListMessages or GoogleOperations.Metadata).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows.Select(row => row.InvocationId).Distinct());
        Assert.All(rows, row =>
        {
            Assert.Equal(CostKind.ExternalProvider, row.Kind);
            Assert.Equal(1, row.Quantity);
            Assert.Equal("request", row.Unit);
            Assert.Equal(key, row.Neuron);
            Assert.Equal(traceId, row.TraceId);
            Assert.Equal(FundingKind.OwnConnection, row.FundingKind);
            Assert.Equal(PricingStatus.Unpriced, row.PricingStatus);
            Assert.Equal(0, row.Micros);
            Assert.Null(row.ListPriceMicros);
            Assert.Equal(ExternalCallState.Settled, row.AttemptState);
        });
        await brain.Get<IConnection>(key).Disconnect();
        Assert.Empty((await gmail.Tools(ct)).Tools);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ToolValidationMatchesTypedCallsAndNeverEchoesUntrustedInput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        using var caller = brain.Owner.AsCaller();
        var gmail = brain.Get<IGmail>(ConnectionKeys.For(brain.Owner.BrainId, "gmail", "invalid"));
        var typed = await gmail.ReadInbox(new(0), ct);
        var tool = await gmail.Run("gmail_read_inbox", "{\"pageSize\":0}", ct);
        Assert.True(tool.IsError);
        Assert.Equal(JsonSerializer.Serialize(typed, Json), tool.Text);

        foreach (var input in new[] { "{", "null", "[]", "{\"pageSize\":25,\"payer\":\"secret-sentinel\"}" })
        {
            var invalid = await gmail.Run("gmail_read_inbox", input, ct);
            Assert.True(invalid.IsError);
            Assert.Equal("invalid_input", JsonSerializer.Deserialize<GmailInboxResult>(invalid.Text, Json)!.Failure!.Code);
            Assert.DoesNotContain("secret-sentinel", invalid.Text, StringComparison.Ordinal);
        }
        Assert.Empty(google.Requests);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gmail.Run("gmail_read_inbox", "{}", cancelled.Token));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ToolDraftReplayAndDisconnectedRecoveryReturnTheTypedReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "draft-tools");
        using var caller = brain.Owner.AsCaller();
        await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
            GmailScopes.Read + " https://www.googleapis.com/auth/gmail.compose", null, "{}"));
        var gmail = brain.Get<IGmail>(key);
        google.ClearRequests();
        var previewTool = await gmail.Run("gmail_preview_draft", JsonSerializer.Serialize(new GmailDraftRequest(["friend@example.test"], [], [], "Review", "Prepared locally."), Json), ct);
        Assert.False(previewTool.IsError);
        var preview = JsonSerializer.Deserialize<GmailDraftPreviewResult>(previewTool.Text, Json)!.Value!;
        Assert.Empty(google.Requests);
        var request = new GmailCreateDraftRequest(preview.PreviewId, "retained-request");
        var createdTool = await gmail.Run("gmail_create_draft", JsonSerializer.Serialize(request, Json), ct);
        Assert.False(createdTool.IsError);
        var created = JsonSerializer.Deserialize<GmailDraftCreationResult>(createdTool.Text, Json)!;
        Assert.Equal(GmailDraftOperationStatus.Completed, created.Value!.Status);
        var accounting = Assert.IsType<GmailDraftAccounting>(created.Value.Accounting);
        var usage = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>($"/brains/{brain.Owner.BrainId}/compute/traces/{accounting.TraceId}", ComputeJson, ct))!;
        var draftCost = Assert.Single(usage.Rows, row => row.Item == GoogleOperations.CreateDraft);
        Assert.Equal(accounting.InvocationId, draftCost.InvocationId);
        Assert.Equal(key, draftCost.Neuron);
        Assert.Equal(FundingKind.OwnConnection, draftCost.FundingKind);
        Assert.Equal(PricingStatus.Unpriced, draftCost.PricingStatus);
        Assert.Equal(JsonSerializer.Serialize(created, Json), JsonSerializer.Serialize(await gmail.CreateDraft(request, ct), Json));
        Assert.Single(google.Requests, call => call.Method == "POST" && call.Path.EndsWith("/drafts", StringComparison.Ordinal));
        await brain.Get<IConnection>(key).Disconnect();
        Assert.Equal("gmail_get_draft_operation", Assert.Single((await gmail.Tools(ct)).Tools).Name);
        var recoveredTool = await gmail.Run("gmail_get_draft_operation", "{\"requestId\":\"retained-request\"}", ct);
        Assert.False(recoveredTool.IsError);
        var recovered = JsonSerializer.Deserialize<GmailDraftOperationResult>(recoveredTool.Text, Json)!;
        Assert.Equal(JsonSerializer.Serialize(created.Value, Json), JsonSerializer.Serialize(recovered.Value, Json));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UnknownAndDefiniteFailureAreToolErrorsWithDurableTypedReceipts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        using var caller = brain.Owner.AsCaller();
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "tool-outcomes");
        await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
            GmailScopes.Read + " https://www.googleapis.com/auth/gmail.compose", null, "{}"));
        var gmail = brain.Get<IGmail>(key);
        foreach (var (reply, status) in new[] { (new GoogleStubReply(200, new { }), GmailDraftOperationStatus.Unknown), (new GoogleStubReply(400), GmailDraftOperationStatus.Failed) })
        {
            google.DraftReplies.Enqueue(reply);
            var preview = (await gmail.PreviewDraft(new(["friend@example.test"], [], [], "Review", "Body"), ct)).Value!;
            var requestId = "outcome-" + status;
            var created = await gmail.Run("gmail_create_draft", JsonSerializer.Serialize(new GmailCreateDraftRequest(preview.PreviewId, requestId), Json), ct);
            Assert.True(created.IsError);
            Assert.Equal(status, JsonSerializer.Deserialize<GmailDraftCreationResult>(created.Text, Json)!.Value!.Status);
            var lookup = await gmail.Run("gmail_get_draft_operation", JsonSerializer.Serialize(new GmailDraftOperationRequest(requestId), Json), ct);
            Assert.True(lookup.IsError);
            Assert.Equal(status, JsonSerializer.Deserialize<GmailDraftOperationResult>(lookup.Text, Json)!.Value!.Status);
        }
    }
}
