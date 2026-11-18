using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Journeys;

[Collection(OsAppHostCollection.Name)]
public sealed class McpScreenUserJourneysFacts(OsAppHostFixture host)
{
    private const string McpScope = "mcp-journey";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private int _requestId;

    [Fact]
    public async Task McpServerExposesScreenAndNeuronTools()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(McpScope, ct);

        await InitializeMcpAsync(ct);

        using var listResult = await SendMcpAsync("tools/list", new { }, ct);
        var toolNames = listResult.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("screen_list", toolNames);
        Assert.Contains("screen_read", toolNames);
        Assert.Contains("screen_act", toolNames);
        Assert.Contains("screen_approve", toolNames);
        Assert.Contains("neurons_list", toolNames);
        Assert.Contains("neurons_call", toolNames);
    }

    [Fact]
    public async Task McpScreenUserJourneysExerciseAppsAndRuntimeUnits()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(McpScope, ct);
        await InitializeMcpAsync(ct);

        // J0: Discover available screens via screen_list
        var listPayload = await CallToolJsonAsync("screen_list", new { }, ct);
        var initialAppIds = listPayload.RootElement
            .EnumerateArray()
            .Select(s => s.GetProperty("appId").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("creator", initialAppIds);
        Assert.Contains("settings", initialAppIds);
        Assert.Contains("ino", initialAppIds);

        // J1: Open Creator via screen_read and deploy a starter unit template via screen_act
        var creatorSnapshot = await CallToolJsonAsync("screen_read", new { appId = "creator" }, ct);
        Assert.Equal("creator", creatorSnapshot.RootElement.GetProperty("appId").GetString());
        var creatorGen = creatorSnapshot.RootElement.GetProperty("generation").GetInt64();
        var seedButtonKey = creatorSnapshot.RootElement
            .GetProperty("controls")
            .EnumerateArray()
            .Select(c => c.GetProperty("key").GetString() ?? "")
            .FirstOrDefault(k => string.Equals(k, "seed:organizer.work.notes", StringComparison.Ordinal))
            ?? creatorSnapshot.RootElement
                .GetProperty("controls")
                .EnumerateArray()
                .Select(c => c.GetProperty("key").GetString() ?? "")
                .FirstOrDefault(k => k.StartsWith("seed:", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(seedButtonKey));

        var seededAlias = seedButtonKey!["seed:".Length..];
        string seedActionRaw;
        using (var seedAction = await CallToolJsonAsync("screen_act", new
        {
            appId = "creator",
            key = seedButtonKey,
            generation = creatorGen,
        }, ct))
        {
            seedActionRaw = seedAction.RootElement.GetRawText();
            Assert.True(seedAction.RootElement.GetProperty("accepted").GetBoolean());
        }

        // J2: Wait for confirmation gate to surface the staged unit install, then approve via screen_approve
        string? installApprovalId = null;
        string lastConfRaw = "";
        for (var attempt = 0; attempt < 80; attempt++)
        {
            using var confPending = await CallToolJsonAsync("screen_read", new { appId = "confirmations" }, ct);
            lastConfRaw = confPending.RootElement.GetRawText();
            if (confPending.RootElement.TryGetProperty("pendingApprovals", out var approvals))
            {
                foreach (var approval in approvals.EnumerateArray())
                {
                    var id = approval.GetProperty("approvalId").GetString() ?? "";
                    var title = approval.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var summary = approval.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    if (id.Contains(seededAlias, StringComparison.OrdinalIgnoreCase)
                        || title.Contains(seededAlias, StringComparison.OrdinalIgnoreCase)
                        || summary.Contains(seededAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        installApprovalId = id;
                        break;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(installApprovalId)) { break; }
            await Task.Delay(250, ct);
        }

        Assert.False(string.IsNullOrWhiteSpace(installApprovalId), $"No pending approval for {seededAlias}. Seed action: {seedActionRaw}. Last confirmations: {lastConfRaw}");

        // Gate 2.0: MCP callers run as Actor.Agent and cannot self-approve RequiresPerson unit installs.
        using (var agentApproveAttempt = await TryCallToolJsonAsync("screen_approve", new
        {
            approvalId = installApprovalId!,
            approved = true,
            reason = "Agent self-approve attempt must be refused",
        }, ct))
        {
            // The refusal now comes back as the tool's answer: accepted false, the person answers confirmations in the shell (by design).
            Assert.NotNull(agentApproveAttempt);
            Assert.False(agentApproveAttempt.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Contains("the person answers it in the shell", agentApproveAttempt.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        // Approve the staged unit install through the human presence-proved HTTP edge.
        using (var personApproveReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{McpScope}/apps/event")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                kind = "confirmation",
                name = $"{McpScope}/{DigitalBrain.Confirmation.IConfirmations.ConfirmationsNeuronKey}",
                planId = installApprovalId!,
                approve = true,
            }),
        })
        {
            using var personApproveRes = await host.Os.SendAsync(personApproveReq, ct);
            Assert.Equal(HttpStatusCode.OK, personApproveRes.StatusCode);
        }

        using (var duplicateApprove = await TryCallToolJsonAsync("screen_approve", new
        {
            approvalId = installApprovalId!,
            approved = true,
            reason = "Duplicate approval guard",
        }, ct))
        {
            if (duplicateApprove is not null)
            {
                Assert.False(duplicateApprove.RootElement.GetProperty("accepted").GetBoolean(), $"Duplicate approve should be false: {duplicateApprove.RootElement.GetRawText()}");
            }
        }

        // Poll screen_read for the deployed runtime unit until it has rendered controls
        JsonDocument? unitSnapshot = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            unitSnapshot?.Dispose();
            unitSnapshot = await TryCallToolJsonAsync("screen_read", new { appId = seededAlias }, ct);
            if (unitSnapshot is not null &&
                unitSnapshot.RootElement.TryGetProperty("controls", out var unitControls) &&
                unitControls.GetArrayLength() > 0)
            {
                break;
            }
            await Task.Delay(250, ct);
        }

        Assert.NotNull(unitSnapshot);
        using (unitSnapshot)
        {
            var controls = unitSnapshot!.RootElement.GetProperty("controls").EnumerateArray().ToArray();
            Assert.True(controls.Length > 0, $"Expected controls for {seededAlias}, got snapshot: {unitSnapshot!.RootElement.GetRawText()}");
            var actionKey = controls
                .Select(c => c.GetProperty("key").GetString() ?? "")
                .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            Assert.False(string.IsNullOrWhiteSpace(actionKey));

            var unitGen = unitSnapshot.RootElement.GetProperty("generation").GetInt64();
            var unitAct = await CallToolJsonAsync("screen_act", new
            {
                appId = seededAlias,
                key = actionKey!,
                generation = unitGen,
            }, ct);
            var unitActText = unitAct.RootElement.GetRawText();
            Assert.True(unitAct.RootElement.GetProperty("accepted").GetBoolean(), $"unitAct rejected: {unitActText}");
            Assert.True(unitAct.RootElement.TryGetProperty("screen", out var updatedUnitScreen), $"unitAct missing screen: {unitActText}");
            Assert.True(updatedUnitScreen.GetProperty("generation").GetInt64() > unitGen, $"Expected generation > {unitGen}: {unitActText}");
        }

        // J3 / J5 / J6: Navigate Settings and read Data, Ino, Confirmations via screen_read / screen_act
        using var settingsSnapshot = await CallToolJsonAsync("screen_read", new { appId = "settings" }, ct);
        Assert.Equal("settings", settingsSnapshot.RootElement.GetProperty("appId").GetString());
        var settingsGen = settingsSnapshot.RootElement.GetProperty("generation").GetInt64();
        var navKey = settingsSnapshot.RootElement
            .GetProperty("controls")
            .EnumerateArray()
            .Select(c => c.GetProperty("key").GetString() ?? "")
            .FirstOrDefault(k => k.StartsWith("nav:", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(navKey))
        {
            using var settingsNav = await CallToolJsonAsync("screen_act", new
            {
                appId = "settings",
                key = navKey!,
                generation = settingsGen,
            }, ct);
            Assert.True(settingsNav.RootElement.GetProperty("accepted").GetBoolean());
        }

        using var dataSnapshot = await CallToolJsonAsync("screen_read", new { appId = "data" }, ct);
        Assert.Equal("data", dataSnapshot.RootElement.GetProperty("appId").GetString());

        using var inoSnapshot = await CallToolJsonAsync("screen_read", new { appId = "ino" }, ct);
        Assert.Equal("ino", inoSnapshot.RootElement.GetProperty("appId").GetString());

        using var confSnapshot = await CallToolJsonAsync("screen_read", new { appId = "confirmations" }, ct);
        Assert.Equal("confirmations", confSnapshot.RootElement.GetProperty("appId").GetString());

        // J4: Approval gate double-resolution safety via screen_approve
        using var approveResult = await CallToolJsonAsync("screen_approve", new
        {
            approvalId = "nonexistent-e2e-approval-check",
            approved = true,
            reason = "e2e verification",
        }, ct);
        Assert.False(approveResult.RootElement.GetProperty("accepted").GetBoolean());
    }

    private async Task InitializeMcpAsync(CancellationToken ct)
    {
        using var init = await SendMcpAsync("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "e2e-mcp-user", version = "1.0" },
        }, ct);
        Assert.True(init.RootElement.TryGetProperty("result", out _));
    }

    private async Task<JsonDocument> CallToolJsonAsync(string name, object arguments, CancellationToken ct)
    {
        var doc = await TryCallToolJsonAsync(name, arguments, ct);
        Assert.NotNull(doc);
        return doc!;
    }

    private async Task<JsonDocument?> TryCallToolJsonAsync(string name, object arguments, CancellationToken ct)
    {
        using var rpc = await SendMcpAsync("tools/call", new { name, arguments }, ct);
        if (!rpc.RootElement.TryGetProperty("result", out var result))
        {
            return null;
        }
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            return null;
        }
        var content = result.GetProperty("content");
        if (content.GetArrayLength() == 0)
        {
            return null;
        }
        var text = content[0].GetProperty("text").GetString();
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private async Task<JsonDocument> SendMcpAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }, Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/brains/{McpScope}/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await host.Os.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true ||
            raw.StartsWith("event:", StringComparison.Ordinal) ||
            raw.StartsWith("data:", StringComparison.Ordinal))
        {
            foreach (var line in raw.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    var jsonPayload = trimmed["data:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(jsonPayload))
                    {
                        return JsonDocument.Parse(jsonPayload);
                    }
                }
            }
        }

        return JsonDocument.Parse(raw);
    }
}
