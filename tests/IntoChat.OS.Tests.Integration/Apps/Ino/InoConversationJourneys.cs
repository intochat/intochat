using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests.Support;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

/// <summary>
/// End-to-end Ino user journeys executed against the real <c>IntoChat.OS.Kernel</c> child-process
/// product host with <see cref="ScriptedOllama"/> at the HTTP socket, real <c>X-IntoChat-Presence</c>
/// proof, real MCP/HTTP edge transport, and zero DI replacements of identity, vault, or redaction.
/// </summary>
public sealed class InoProductHostFixture : IAsyncLifetime
{
    public ScriptedOllama Ollama { get; private set; } = null!;

    public KernelProcessHarness Host { get; private set; } = null!;

    public string Scope => Host.Scope;

    public HttpClient Http => Host.Client;

    public async ValueTask InitializeAsync()
    {
        Ollama = ScriptedOllama.Start();
        Host = await KernelProcessHarness.StartAsync(
            new KernelProcessOptions
            {
                OllamaEndpoint = Ollama.BaseAddress,
                DefaultModel = Ollama.Models[0],
            },
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }

        if (Ollama is not null)
        {
            await Ollama.DisposeAsync();
        }
    }
}

public sealed class InoConversationJourneys(InoProductHostFixture host) : IClassFixture<InoProductHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Ino's messages live in its conversation thread slot; MCP and HTTP readers see its controls under the slot key.
    private const string ComposerControl = InoView.ConversationKey + "/" + ChatThreadProps.ComposerKey;
    private const string StopControl = InoView.ConversationKey + "/" + ChatThreadProps.StopKey;
    private const string ErrorRetryControl = InoView.ConversationKey + "/" + ChatThreadProps.ErrorRetryKey;
    private const string MessageRetryControlPrefix = InoView.ConversationKey + "/retry:";

    [Fact]
    public async Task PresenceProofIsEnforcedAtBothHttpAndMcpEdges()
    {
        using var unproved = host.Host.CreateClient(withPresence: false);
        using var appsDenied = await unproved.GetAsync($"/brains/{host.Scope}/apps", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, appsDenied.StatusCode);

        using var forged = host.Host.CreateClient(withPresence: false);
        forged.DefaultRequestHeaders.Add(PresenceProof.HeaderName, "forged-presence-proof");
        using var screenDenied = await forged.GetAsync($"/brains/{host.Scope}/screens/ino", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, screenDenied.StatusCode);

        using var mcpDeniedReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{host.Scope}/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        using var mcpDenied = await unproved.SendAsync(mcpDeniedReq, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, mcpDenied.StatusCode);

        await using var mcp = await CreateMcpClientAsync();
        var listResult = await mcp.CallToolAsync("screen_list", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.True(listResult.IsError != true, ToolText(listResult));
        Assert.Contains("ino", ToolText(listResult), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultiTurnConversationStreamsWorkingStateThenPreservesTranscriptOverHttpAndMcp()
    {
        await ResetInoConversationAsync();
        var model = host.Ollama.Models[0];
        var held = host.Ollama.HoldNext(model);

        // Idle: no stop is offered.
        Assert.DoesNotContain(StopControl, (await ReadInoScreenAsync()).Controls);
        await SubmitInoPromptAsync("First check: summarize kernel health.");
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var whileWorking = await WaitForScreenAsync(
            s => s.RawJson.Contains("\"working\"", StringComparison.OrdinalIgnoreCase)
                || s.Controls.Contains(StopControl, StringComparer.Ordinal),
            "Ino did not enter working state with stop control while turn was held in ScriptedOllama");
        Assert.Contains(StopControl, whileWorking.Controls);

        held.Release("Kernel health is nominal across all silos.");

        var afterTurn1 = await WaitForScreenAsync(
            s => s.RawJson.Contains("Kernel health is nominal across all silos.", StringComparison.Ordinal)
                && s.RawJson.Contains("\"ready\"", StringComparison.OrdinalIgnoreCase),
            "Ino did not transition back to ready with first reply");
        Assert.DoesNotContain(StopControl, afterTurn1.Controls);

        host.Ollama.EnqueueReply("Follow-up confirmed: silo metrics remain healthy.", model);
        await using var mcp = await CreateMcpClientAsync();
        var actResult = await mcp.CallToolAsync(
            "screen_act",
            new Dictionary<string, object?>
            {
                ["appId"] = "ino",
                ["key"] = ComposerControl,
                ["value"] = "Any warnings since that first check?",
            },
            cancellationToken: Ct);
        Assert.True(actResult.IsError != true, ToolText(actResult));

        var afterTurn2 = await WaitForMcpInoScreenAsync(
            mcp,
            json => json.Contains("Follow-up confirmed: silo metrics remain healthy.", StringComparison.Ordinal),
            "MCP screen_read did not observe second Ino reply");
        Assert.Contains("First check: summarize kernel health.", afterTurn2, StringComparison.Ordinal);
        Assert.Contains("Any warnings since that first check?", afterTurn2, StringComparison.Ordinal);

        Assert.True(host.Ollama.ChatRequests.Count >= 2);
        var turn2Request = host.Ollama.ChatRequests[^1];
        Assert.True(turn2Request.HasMessageContaining("user", "First check: summarize kernel health."));
        Assert.True(turn2Request.HasMessageContaining("assistant", "Kernel health is nominal across all silos."));
    }

    [Fact]
    public async Task MemoryPreferenceToolCallPersistsAndIsRecalledInFollowUpConversation()
    {
        await ResetInoConversationAsync();
        var model = host.Ollama.Models[0];

        host.Ollama.EnqueueToolCall(
            "ai_memory_remember_preference",
            new
            {
                scope = "owner",
                subject = "indentation-style",
                body = "Always format C# files with 4 spaces indentation and no tabs.",
                importance = 9,
            },
            model: model);
        host.Ollama.EnqueueReply("Saved your 4-space C# indentation preference to durable memory.", model);

        await SubmitInoPromptAsync("Remember my coding rule: always format C# files with 4 spaces indentation.");

        var afterRemember = await WaitForScreenAsync(
            s => s.RawJson.Contains("Saved your 4-space C# indentation preference to durable memory.", StringComparison.Ordinal),
            "Ino did not render assistant reply after RememberPreference tool call");
        Assert.Contains("\"activities\"", afterRemember.RawJson, StringComparison.Ordinal);
        Assert.Contains("\"done\"", afterRemember.RawJson, StringComparison.Ordinal);

        await StartFreshConversationAsync();
        host.Ollama.ClearRequests();
        host.Ollama.EnqueueReply("Per your saved rule, use 4 spaces indentation for C#.", model);

        await SubmitInoPromptAsync("What is my indentation-style rule for formatting C#?");

        _ = await WaitForScreenAsync(
            s => s.RawJson.Contains("Per your saved rule, use 4 spaces indentation for C#.", StringComparison.Ordinal),
            "Ino did not reply on new conversation using memory recall");

        var recallRequest = Assert.Single(host.Ollama.ChatRequests);
        Assert.True(
            recallRequest.HasSystemMessageContaining("4 spaces indentation")
            || recallRequest.RawJson.Contains("4 spaces indentation", StringComparison.OrdinalIgnoreCase),
            $"Expected durable IMemory preference to be recalled into /api/chat context. Actual payload:\n{recallRequest.RawJson}");
    }

    [Fact]
    public async Task ProviderFailureIsSurfacedOnScreenAndRecoversOnRetry()
    {
        await ResetInoConversationAsync();
        var model = host.Ollama.Models[0];

        host.Ollama.EnqueueFailure(HttpStatusCode.ServiceUnavailable, "Simulated upstream Ollama 503 outage");
        await SubmitInoPromptAsync("Diagnose cluster heartbeat latency.");

        var errored = await WaitForScreenAsync(
            s => s.Controls.Any(key => key.StartsWith(MessageRetryControlPrefix, StringComparison.Ordinal))
                && s.RawJson.Contains("The model provider did not answer.", StringComparison.Ordinal),
            "Ino did not surface the failed reply's reason and retry after Ollama 503 failure");

        // The failed reply carries its own retry, which resends that turn.
        var retryKey = errored.Controls.Last(key => key.StartsWith(MessageRetryControlPrefix, StringComparison.Ordinal));

        host.Ollama.EnqueueReply("Recovered diagnostics: cluster heartbeat is 4ms.", model);

        using var retryResponse = await host.Http.PostAsJsonAsync(
            $"/brains/{host.Scope}/screens/ino/act",
            new { key = retryKey, value = (string?)null, generation = errored.Generation },
            Ct);
        Assert.True(
            retryResponse.IsSuccessStatusCode,
            $"Retry action failed with {(int)retryResponse.StatusCode}: {await retryResponse.Content.ReadAsStringAsync(Ct)}");

        var recovered = await WaitForScreenAsync(
            s => s.RawJson.Contains("Recovered diagnostics: cluster heartbeat is 4ms.", StringComparison.Ordinal)
                && s.RawJson.Contains("\"ready\"", StringComparison.OrdinalIgnoreCase),
            "Ino did not recover from Ollama failure after Retry action");
        Assert.DoesNotContain(ErrorRetryControl, recovered.Controls);
    }

    [Fact]
    public async Task FlowApprovalAppearsOnInoScreenIsRefusedToMcpAndCompletesOnlyViaPersonPresence()
    {
        await ResetInoConversationAsync();
        var model = host.Ollama.Models[0];
        var toolInvoked = false;

        host.Ollama.Respond(request =>
        {
            if (request.HasMessageContaining("user", "Start release checklist approval"))
            {
                if (!toolInvoked && request.Messages.All(m => !string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)))
                {
                    toolInvoked = true;
                    var innerJson = JsonSerializer.Serialize(new PlanInner { Goal = "Release checklist approval" }, PlanFlow.Json);
                    var args = JsonSerializer.SerializeToElement(
                        new
                        {
                            type = PlanFlow.Alias,
                            title = "Release Checklist",
                            parentFlowId = (string?)null,
                            innerJson,
                        },
                        JsonSerializerOptions.Web);
                    return new ScriptedOllamaOutcome.ToolCall("flows_start", args, Model: model);
                }

                return new ScriptedOllamaOutcome.Reply("Release Checklist plan started; review the pending approval.", model);
            }

            if (request.HasMessageContaining("user", "Release checklist approval"))
            {
                return new ScriptedOllamaOutcome.Reply(
                    """{ "subtasks": [ { "title": "Record release note", "type": "flow.note", "inner": {} } ] }""",
                    model);
            }

            return new ScriptedOllamaOutcome.Reply("Unexpected prompt", model);
        });

        await SubmitInoPromptAsync("Start release checklist approval.");

        var withApproval = await WaitForScreenAsync(
            s => s.RawJson.Contains("approval-", StringComparison.Ordinal)
                && s.RawJson.Contains("Release Checklist", StringComparison.Ordinal),
            "Pending PlanFlow approval did not surface on Ino screen");

        var planId = ExtractPlanIdFromScreen(withApproval.RawJson);
        Assert.False(string.IsNullOrWhiteSpace(planId));

        await using var mcp = await CreateMcpClientAsync();
        var approveAttempt = await mcp.CallToolAsync(
            "screen_approve",
            new Dictionary<string, object?>
            {
                ["approvalId"] = planId,
                ["approved"] = true,
                ["reason"] = "Agent attempting self-approval",
            },
            cancellationToken: Ct);
        Assert.True(approveAttempt.IsError == true || ToolText(approveAttempt).Contains("person", StringComparison.OrdinalIgnoreCase));

        using var stillPending = await host.Http.GetAsync($"/brains/{host.Scope}/screens/ino", Ct);
        stillPending.EnsureSuccessStatusCode();
        Assert.Contains($"approval-{planId}", await stillPending.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var personApprove = await host.Http.PostAsJsonAsync(
            $"/brains/{host.Scope}/confirmations/{Uri.EscapeDataString(planId)}",
            new { approve = true },
            Ct);
        Assert.Equal(HttpStatusCode.OK, personApprove.StatusCode);

        _ = await WaitForScreenAsync(
            s => !s.RawJson.Contains($"approval-{planId}", StringComparison.Ordinal),
            "Pending approval remained on Ino screen after person approval with X-IntoChat-Presence");
    }

    private async Task<McpClient> CreateMcpClientAsync()
        => await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.Host.BaseAddress, $"/brains/{host.Scope}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                host.Http,
                ownsHttpClient: false),
            cancellationToken: Ct);

    private async Task ResetInoConversationAsync()
    {
        host.Ollama.ClearResponses();
        host.Ollama.ClearRequests();
        await EnsureInoOpenAsync();
        await StartFreshConversationAsync();
        host.Ollama.ClearRequests();
    }

    private async Task EnsureInoOpenAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var appResponse = await host.Http.GetAsync($"/brains/{host.Scope}/apps/ino", Ct);
            if (appResponse.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(100, Ct);
        }

        if (await TryReadInoScreenAsync() is { } existing
            && existing.Controls.Contains(ComposerControl, StringComparer.Ordinal))
        {
            return;
        }

        using var openResponse = await host.Http.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/ino/request",
            new { operation = "open", input = "{}" },
            Ct);
        Assert.True(
            openResponse.StatusCode == HttpStatusCode.Accepted || openResponse.IsSuccessStatusCode,
            $"Open Ino returned {(int)openResponse.StatusCode}: {await openResponse.Content.ReadAsStringAsync(Ct)}");

        _ = await WaitForScreenAsync(
            s => s.Controls.Contains(ComposerControl, StringComparer.Ordinal),
            "Ino composer never became available");
    }

    private async Task StartFreshConversationAsync()
    {
        var previous = await ReadInoScreenAsync();
        if (IsCleanEmptyScreen(previous.RawJson))
        {
            return;
        }

        using var newResponse = await host.Http.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/ino/request",
            new { operation = "new-conversation", input = "{}" },
            Ct);
        Assert.True(
            newResponse.StatusCode == HttpStatusCode.Accepted || newResponse.IsSuccessStatusCode,
            $"new-conversation returned {(int)newResponse.StatusCode}: {await newResponse.Content.ReadAsStringAsync(Ct)}");

        _ = await WaitForScreenAsync(
            s => s.Generation > previous.Generation
                && s.Controls.Contains(ComposerControl, StringComparer.Ordinal)
                && IsCleanEmptyScreen(s.RawJson),
            "Ino did not settle after new-conversation");
    }

    private async Task<InoScreenSnapshot?> TryReadInoScreenAsync()
    {
        using var response = await host.Http.GetAsync($"/brains/{host.Scope}/screens/ino", Ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var raw = await response.Content.ReadAsStringAsync(Ct);
        return ParseScreenSnapshot(raw);
    }

    private static InoScreenSnapshot ParseScreenSnapshot(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var generation = root.TryGetProperty("generation", out var gen) && gen.TryGetInt64(out var g) ? g : 0L;
        var controls = root.TryGetProperty("controls", out var ctrls)
            ? ctrls.EnumerateArray().Select(c => c.GetProperty("key").GetString() ?? string.Empty).ToArray()
            : [];
        return new InoScreenSnapshot(generation, controls, raw);
    }

    private static bool IsCleanEmptyScreen(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("view", out var view) || !view.TryGetProperty("root", out var node))
        {
            return false;
        }

        // The thread may be wrapped in a single-child filling column; clean means nothing above/below it and no messages yet.
        if (node.TryGetProperty("kind", out var rootKind) && rootKind.GetString() == "column"
            && node.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array
            && children.GetArrayLength() == 1)
        {
            node = children[0];
        }

        return node.TryGetProperty("kind", out var kind) && kind.GetString() == "chatThreadView"
            && node.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array
            && messages.GetArrayLength() == 0;
    }

    private async Task SubmitInoPromptAsync(string prompt)
    {
        var current = await ReadInoScreenAsync();
        using var actResponse = await host.Http.PostAsJsonAsync(
            $"/brains/{host.Scope}/screens/ino/act",
            new { key = ComposerControl, value = prompt, generation = current.Generation },
            Ct);
        Assert.True(
            actResponse.IsSuccessStatusCode,
            $"POST /screens/ino/act failed with {(int)actResponse.StatusCode}: {await actResponse.Content.ReadAsStringAsync(Ct)}");
    }

    private async Task<InoScreenSnapshot> ReadInoScreenAsync()
    {
        using var response = await host.Http.GetAsync($"/brains/{host.Scope}/screens/ino", Ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(Ct);
        return ParseScreenSnapshot(raw);
    }

    private async Task<InoScreenSnapshot> WaitForScreenAsync(Func<InoScreenSnapshot, bool> predicate, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        InoScreenSnapshot last = await ReadInoScreenAsync();
        while (!predicate(last) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
            last = await ReadInoScreenAsync();
        }

        Assert.True(predicate(last), $"{failureMessage}. Last snapshot:\n{last.RawJson}");
        return last;
    }

    private static async Task<string> WaitForMcpInoScreenAsync(McpClient mcp, Func<string, bool> predicate, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var last = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await mcp.CallToolAsync("screen_read", new Dictionary<string, object?> { ["appId"] = "ino" }, cancellationToken: Ct);
            last = ToolText(result);
            if (result.IsError != true && predicate(last))
            {
                return last;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"{failureMessage}. Last MCP screen_read:\n{last}");
        return last;
    }

    private static string? ExtractPlanIdFromScreen(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return FindPlanId(doc.RootElement);
    }

    private static string? FindPlanId(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("planId", out var planProp) && planProp.ValueKind == JsonValueKind.String)
                {
                    return planProp.GetString();
                }

                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("key")
                        && prop.Value.ValueKind == JsonValueKind.String
                        && prop.Value.GetString() is { } key
                        && key.StartsWith("approval-", StringComparison.Ordinal))
                    {
                        return key["approval-".Length..];
                    }

                    if (FindPlanId(prop.Value) is { } found)
                    {
                        return found;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindPlanId(item) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    private static string ToolText(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed record InoScreenSnapshot(long Generation, string[] Controls, string RawJson);
}
