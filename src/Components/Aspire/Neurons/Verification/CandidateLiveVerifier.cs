using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.Aspire.Candidates;
using IntoChat.Aspire.Deployment;
using IntoChat.OS;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.Aspire.Verification;

/// <summary>
/// Unified live verifier for both <see cref="ICandidateVerifier"/> (<see cref="CandidateNeuron"/>) and
/// <see cref="IEngineerCandidateHostVerifier"/> (<c>EngineerFlow</c> / <c>EngineerLiveVerifier</c>).
/// Boots candidates directly from an already-committed <c>WorkTreeLease</c> SHA (avoiding the double-commit
/// <c>Prepare</c> bug and pure-deletion mismatch) and executes real <c>/brains/{brainId}/mcp</c> JSON-RPC checks
/// (<c>neurons_context</c>, <c>neurons_list</c>, <c>neurons_call</c>), Playwright UI checks when <c>TouchesUi</c>,
/// and OTel span verification.
/// </summary>
internal sealed class CandidateLiveVerifier : ICandidateVerifier, IEngineerCandidateHostVerifier
{
    private const string AppContract = "IntoChat.OS.Modules.Apps.IApp";
    private const string ReconcilerContract = "IntoChat.OS.Modules.Apps.IFeatureReconciler";
    private static readonly TimeSpan DefaultHealthBudget = TimeSpan.FromMinutes(2);

    private readonly ICandidateHost? _candidateHost;
    private readonly IIntoChatOSBrains? _brains;
    private readonly IClusterHost? _cluster;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;
    private readonly TimeSpan _healthBudget;
    private readonly ILogger _logger;

    private sealed record Check(string Name, Func<CancellationToken, Task<(bool Passed, string Detail)>> Run);

    [ActivatorUtilitiesConstructor]
    public CandidateLiveVerifier(
        ICandidateHost? candidateHost = null,
        IIntoChatOSBrains? brains = null,
        IClusterHost? cluster = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<CandidateLiveVerifier>? logger = null)
        : this(candidateHost, httpClient: null, DefaultHealthBudget, brains, cluster, httpClientFactory, logger)
    {
    }

    internal CandidateLiveVerifier(
        ICandidateHost? candidateHost,
        HttpClient? httpClient,
        TimeSpan? healthBudget = null,
        IIntoChatOSBrains? brains = null,
        IClusterHost? cluster = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger? logger = null)
    {
        _candidateHost = candidateHost;
        _brains = brains;
        _cluster = cluster;
        _httpClientFactory = httpClientFactory;
        _httpClient = httpClient;
        _healthBudget = healthBudget is { } b && b > TimeSpan.Zero ? b : DefaultHealthBudget;
        _logger = logger ?? OsConsoleLog.For<CandidateLiveVerifier>();
    }

    public bool IsConfigured => _candidateHost is not null;

    public string HostKind => _candidateHost?.GetType().Name ?? "Unconfigured";

    public async Task<VerificationReport> Verify(
        string brainId,
        string sha,
        CandidateEndpoints endpoints,
        VerificationPlan plan,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(plan);
        if (_brains is null || _cluster is null)
        {
            throw new InvalidOperationException("CandidateLiveVerifier requires IIntoChatOSBrains and IClusterHost for ICandidateVerifier.Verify.");
        }

        var clock = Stopwatch.StartNew();
        var brain = brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId);
        var server = _brains.Get<IMcpToolConnection>(IMcpToolConnection.NeuronIdOf(brain, $"candidate-{sha}").FullKey());
        List<VerificationCheck> checks = [];
        try { await server.ConnectServer(new McpConnectionConfig($"{endpoints.Mcp}/brains/{brainId}/mcp", $"candidate {sha}"), ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Connecting to candidate {Sha} MCP server failed.", sha);
            checks.Add(new("connect", false, error.Message));
            return new([.. checks], clock.Elapsed);
        }

        try
        {
            foreach (var check in Checks(brain, sha, plan, server))
            {
                VerificationCheck outcome;
                try
                {
                    var (passed, detail) = await check.Run(ct);
                    outcome = new(check.Name, passed, detail);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogWarning(error, "Candidate {Sha} verification check '{Check}' failed.", sha, check.Name);
                    outcome = new(check.Name, false, error.Message);
                }

                checks.Add(outcome);
                if (!outcome.Passed) { break; }
            }
        }
        finally
        {
            try { await server.DisconnectServer(CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning(error, "Disconnecting candidate {Sha}'s MCP server failed", sha);
            }
        }

        return new([.. checks], clock.Elapsed);
    }

    public async Task<EngineerLiveReport> VerifyCandidateAsync(
        CandidateLiveVerificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_candidateHost is null)
        {
            const string detail = "Candidate host is not configured; fail-closed live verification refused.";
            return FailReport(
                [new EngineerLiveProbe("candidate-host-required", "host", false, 503, detail)],
                ["span candidate.host status=unconfigured passed=False"],
                detail,
                http5xx: 1,
                errorSpans: 0,
                errorLogs: 1);
        }

        var rawHealthPath = string.IsNullOrWhiteSpace(request.HealthPath) ? "/health" : request.HealthPath;
        if (!TryValidateRelativeHealthPath(rawHealthPath, out var validHealthPath, out var healthPathError))
        {
            return FailReport(
                [new EngineerLiveProbe("candidate-http-health", "host", false, 400, healthPathError!)],
                ["span candidate.http_probe path=invalid passed=False"],
                healthPathError!,
                http4xx: 1,
                errorSpans: 1,
                errorLogs: 1);
        }

        var hasCommittedSha = !string.IsNullOrWhiteSpace(request.CommitSha);
        var sha = NormalizeCandidateSha(request.CommitSha, request.WorktreePath);
        var brainId = string.IsNullOrWhiteSpace(request.BrainId) ? "owner" : request.BrainId.Trim();
        var probes = new List<EngineerLiveProbe>();
        var traces = new List<string>();
        var published = false;
        var started = false;
        var count4xx = 0;
        var count5xx = 0;
        var errorSpans = 0;
        var errorLogs = 0;

        try
        {
            // Boot directly from EngineerFlow's already-committed WorkTreeLease SHA.
            // Only invoke Prepare(...) for uncommitted ad-hoc worktrees (fixes the double-commit "nothing to commit" bug
            // and pure-deletion path mismatch).
            if (!hasCommittedSha)
            {
                var stagedFiles = ReadStagedTouchedFiles(request.WorktreePath, request.ChangedFiles);
                if (stagedFiles.Count > 0)
                {
                    var prepared = await _candidateHost.Prepare(sha, request.Goal, stagedFiles, ct).ConfigureAwait(false);
                    sha = prepared.Sha;
                    published = true;
                    traces.Add($"span candidate.prepare host={HostKind} sha={sha} files={stagedFiles.Count} passed=True");
                }
            }

            published = true;
            await _candidateHost.Publish(sha, ct).ConfigureAwait(false);
            traces.Add($"span candidate.publish host={HostKind} sha={sha} passed=True");

            started = true;
            var running = await _candidateHost.Run(sha, ct).ConfigureAwait(false);
            traces.Add($"span candidate.run host={HostKind} sha={sha} container={running.Container} passed=True");

            await _candidateHost.WaitHealthy(sha, _healthBudget, ct).ConfigureAwait(false);
            probes.Add(new EngineerLiveProbe(
                "candidate-host-health",
                "host",
                true,
                200,
                $"{HostKind} started isolated candidate '{running.Container}' ({sha}) and passed readiness check."));
            traces.Add($"span candidate.healthy host={HostKind} sha={sha} passed=True");

            // 1. Real HTTP readiness probe on candidate UI endpoint.
            var uiBaseUrl = running.Endpoints.Ui.TrimEnd('/');
            var uiProbe = await ProbeHttpAsync("candidate-http-health", "host", uiBaseUrl + validHealthPath, ct).ConfigureAwait(false);
            RecordProbe(uiProbe, probes, traces, $"span candidate.http_probe url={uiBaseUrl}{validHealthPath}", ref count4xx, ref count5xx, ref errorLogs);

            // 2. Real MCP JSON-RPC 2.0 checks against /brains/{brainId}/mcp (neurons_context, neurons_list, neurons_call).
            var mcpBase = (!string.IsNullOrWhiteSpace(running.Endpoints.Mcp) ? running.Endpoints.Mcp : running.Endpoints.Ui).TrimEnd('/');
            var mcpUrl = $"{mcpBase}/brains/{Uri.EscapeDataString(brainId)}/mcp";
            var (mcpProbesPassed, m4xx, m5xx, mErr) = await RunMcpJsonRpcChecksAsync(mcpUrl, brainId, request.McpCalls, probes, traces, ct).ConfigureAwait(false);
            count4xx += m4xx;
            count5xx += m5xx;
            errorLogs += mErr;

            // 3. Real UI surface check when TouchesUi is set (headless Playwright DOM check runs in EngineerLiveVerifier).
            var playwrightPassed = uiProbe.Passed;
            if (request.TouchesUi)
            {
                var uiRootProbe = await ProbeHttpAsync("candidate-ui-root", "playwright", uiBaseUrl + "/", ct).ConfigureAwait(false);
                RecordProbe(uiRootProbe, probes, traces, $"span candidate.ui_root url={uiBaseUrl}/", ref count4xx, ref count5xx, ref errorLogs);
                if (!uiRootProbe.Passed) { playwrightPassed = false; }
            }

            // 4. Optional cluster OTel span check when IClusterHost is wired.
            if (_cluster is not null)
            {
                try
                {
                    var errorSpanHits = await _cluster.Spans($"candidate-{sha}", "error", ct).ConfigureAwait(false);
                    var spanOk = errorSpanHits == 0;
                    if (!spanOk) { errorSpans += errorSpanHits; }
                    probes.Add(new EngineerLiveProbe(
                        "candidate-otel-spans",
                        "otel",
                        spanOk,
                        spanOk ? 200 : 500,
                        string.Create(CultureInfo.InvariantCulture, $"Candidate 'candidate-{sha}' reported {errorSpanHits} OTel error span(s).")));
                    traces.Add(string.Create(CultureInfo.InvariantCulture, $"span candidate.otel_audit errors={errorSpanHits} passed={spanOk}"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Optional cluster OTel span query skipped for candidate '{Sha}'.", sha);
                }
            }

            var hostHealthy = uiProbe.Passed && count4xx == 0 && count5xx == 0;
            var succeeded = hostHealthy && mcpProbesPassed && playwrightPassed && errorSpans == 0 && errorLogs == 0;
            if (!succeeded && errorSpans == 0) { errorSpans = 1; }

            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"LiveVerify={succeeded} · Host={HostKind} ({sha}, container={running.Container}), MCP={mcpProbesPassed}, Playwright={playwrightPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}.");

            return new EngineerLiveReport(
                Succeeded: succeeded,
                HostHealthy: hostHealthy,
                McpProbesPassed: mcpProbesPassed,
                PlaywrightPassed: playwrightPassed,
                Http4xxCount: count4xx,
                Http5xxCount: count5xx,
                ErrorSpanCount: errorSpans,
                ErrorLogCount: errorLogs,
                Probes: [.. probes],
                TraceSummaries: [.. traces],
                Summary: summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Candidate host '{HostKind}' verification failed for '{sha}'.");
            var stage = ex is CandidateHostException che ? che.Stage : "verify";
            var detail = $"{HostKind} failed at stage '{stage}' for '{sha}': {ex.Message}";
            probes.Add(new EngineerLiveProbe("candidate-host-startup", "host", false, 500, detail));
            traces.Add($"span candidate.{stage} host={HostKind} sha={sha} status=ERROR error={ex.GetType().Name}");
            return FailReport(probes, traces, detail, count4xx, Math.Max(1, count5xx), 1, Math.Max(1, errorLogs));
        }
        finally
        {
            if (published || started)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _candidateHost.Remove(sha, cleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogSwallowed(cleanupEx, $"Best-effort candidate cleanup failed for '{sha}' on '{HostKind}'.");
                }
            }
        }
    }

    private async Task<(bool Passed, int Count4xx, int Count5xx, int ErrorLogs)> RunMcpJsonRpcChecksAsync(
        string mcpUrl,
        string brainId,
        IReadOnlyList<CandidateMcpCall>? plannedCalls,
        List<EngineerLiveProbe> probes,
        List<string> traces,
        CancellationToken ct)
    {
        var allPassed = true;
        int count4xx = 0, count5xx = 0, errorLogs = 0;

        // 1. Always verify neurons_context answers for the target brainId.
        var ctxProbe = await CallMcpToolAsync(
            mcpUrl,
            probeName: "candidate-mcp-context",
            toolName: "neurons_context",
            arguments: new Dictionary<string, object?>(),
            expectedSubstring: brainId,
            ct).ConfigureAwait(false);
        RecordProbe(ctxProbe, probes, traces, $"span candidate.mcp_context url={mcpUrl}", ref count4xx, ref count5xx, ref errorLogs);
        if (!ctxProbe.Passed) { return (false, count4xx, count5xx, errorLogs); }

        // 2. Execute planned MCP calls (neurons_list / neurons_call) derived from changed files.
        if (plannedCalls is { Count: > 0 })
        {
            foreach (var call in plannedCalls)
            {
                if (string.Equals(call.Target, "neurons_context", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string toolName;
                Dictionary<string, object?> args;
                if (string.Equals(call.Method, "neurons_list", StringComparison.OrdinalIgnoreCase))
                {
                    toolName = "neurons_list";
                    args = new() { ["contract"] = AppContract };
                }
                else
                {
                    var brain = brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId);
                    toolName = "neurons_call";
                    args = new()
                    {
                        ["contract"] = AppContract,
                        ["key"] = new DigitalBrain.NeuronId(brain, call.Target, call.Target).FullKey(),
                        ["method"] = call.Method,
                        ["arguments"] = "[]",
                    };
                }

                var callProbe = await CallMcpToolAsync(
                    mcpUrl,
                    probeName: $"candidate-mcp-{call.Target}",
                    toolName: toolName,
                    arguments: args,
                    expectedSubstring: null,
                    ct).ConfigureAwait(false);
                RecordProbe(callProbe, probes, traces, $"span candidate.mcp_call tool={toolName} target={call.Target}", ref count4xx, ref count5xx, ref errorLogs);
                if (!callProbe.Passed) { allPassed = false; }
            }
        }
        else
        {
            // Default behavioral check: neurons_list returns without error.
            var listProbe = await CallMcpToolAsync(
                mcpUrl,
                probeName: "candidate-mcp-list",
                toolName: "neurons_list",
                arguments: new Dictionary<string, object?> { ["contract"] = AppContract },
                expectedSubstring: null,
                ct).ConfigureAwait(false);
            RecordProbe(listProbe, probes, traces, $"span candidate.mcp_list url={mcpUrl}", ref count4xx, ref count5xx, ref errorLogs);
            if (!listProbe.Passed) { allPassed = false; }
        }

        return (allPassed, count4xx, count5xx, errorLogs);
    }

    private async Task<EngineerLiveProbe> CallMcpToolAsync(
        string mcpUrl,
        string probeName,
        string toolName,
        Dictionary<string, object?> arguments,
        string? expectedSubstring,
        CancellationToken ct)
    {
        if (_httpClientFactory is null && _httpClient is null)
        {
            return new EngineerLiveProbe(probeName, "mcp", false, 503, $"No HttpClient or IHttpClientFactory configured for MCP '{toolName}' at '{mcpUrl}'; fail-closed policy refused.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = toolName, arguments },
        });

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            HttpClient? ownedClient = null;
            try
            {
                if (_httpClientFactory is not null)
                {
                    ownedClient = _httpClientFactory.CreateClient();
                    ownedClient.Timeout = TimeSpan.FromSeconds(20);
                    response = await ownedClient.PostAsync(new Uri(mcpUrl), content, ct).ConfigureAwait(false);
                }
                else
                {
                    response = await _httpClient!.PostAsync(new Uri(mcpUrl), content, ct).ConfigureAwait(false);
                }

                using (response)
                {
                    var code = (int)response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (code is < 200 or >= 300)
                    {
                        return new EngineerLiveProbe(probeName, "mcp", false, code, $"MCP {toolName} -> HTTP {code}: {Truncate(body, 200)}");
                    }

                    return ValidateMcpJsonRpcBody(probeName, toolName, body, expectedSubstring, code);
                }
            }
            finally
            {
                ownedClient?.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Candidate MCP JSON-RPC call '{toolName}' failed for '{mcpUrl}'.");
            return new EngineerLiveProbe(probeName, "mcp", false, 503, $"MCP {toolName} failed for '{mcpUrl}': {ex.Message}");
        }
    }

    internal static EngineerLiveProbe ValidateMcpJsonRpcBody(
        string probeName,
        string toolName,
        string body,
        string? expectedSubstring,
        int httpStatus)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new EngineerLiveProbe(probeName, "mcp", false, 502, $"MCP {toolName} returned empty JSON-RPC response.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new EngineerLiveProbe(probeName, "mcp", false, 502, $"MCP {toolName} returned non-object JSON.");
            }

            if (root.TryGetProperty("error", out var rpcErr) && rpcErr.ValueKind != JsonValueKind.Null)
            {
                return new EngineerLiveProbe(probeName, "mcp", false, 500, $"MCP {toolName} JSON-RPC error: {Truncate(rpcErr.GetRawText(), 200)}");
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return new EngineerLiveProbe(probeName, "mcp", false, 502, $"MCP {toolName} missing JSON-RPC 'result' object.");
            }

            if (result.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
            {
                return new EngineerLiveProbe(probeName, "mcp", false, 500, $"MCP {toolName} returned isError=true: {Truncate(result.GetRawText(), 200)}");
            }

            var rawResult = result.GetRawText();
            if (!string.IsNullOrWhiteSpace(expectedSubstring) && !rawResult.Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return new EngineerLiveProbe(probeName, "mcp", false, 500, $"MCP {toolName} answer did not contain expected '{expectedSubstring}': {Truncate(rawResult, 200)}");
            }

            return new EngineerLiveProbe(probeName, "mcp", true, httpStatus, $"MCP {toolName} -> HTTP {httpStatus} ok.");
        }
        catch (JsonException ex)
        {
            return new EngineerLiveProbe(probeName, "mcp", false, 502, $"MCP {toolName} returned invalid JSON: {ex.Message}");
        }
    }

    private IEnumerable<Check> Checks(DigitalBrain.BrainId brain, string sha, VerificationPlan plan, IMcpToolConnection server)
    {
        yield return new("context", async ct =>
        {
            var context = await server.Run("neurons_context", "{}", ct);
            if (context.IsError) { return (false, context.Text); }
            using var document = JsonDocument.Parse(context.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("brainId", out var id) || id.ValueKind != JsonValueKind.String)
            { return (false, $"no brainId in the candidate's answer: {context.Text}"); }
            var answered = id.GetString();
            return (answered == brain.Value, $"candidate answers for brain '{answered}'");
        });
        yield return new("activation", async ct =>
        {
            var reconcile = JsonSerializer.Serialize(new { contract = ReconcilerContract, key = new DigitalBrain.NeuronId(brain, "apps.reconciler", "apps-reconciler").FullKey(), method = "Reconcile", arguments = "[]" });
            var reconciled = await server.Run("neurons_call", reconcile, ct);
            if (reconciled.IsError) { return (false, $"reconciling features failed: {reconciled.Text}"); }
            var apps = await server.Run("neurons_list", JsonSerializer.Serialize(new { contract = AppContract }), ct);
            return (!apps.IsError, apps.IsError ? apps.Text : "the brain's apps list from the copied state");
        });
        foreach (var call in plan.Calls)
        {
            yield return new($"call {call.Contract} {call.Key} {call.Method}", async ct =>
            {
                using (var arguments = JsonDocument.Parse(call.Arguments))
                {
                    if (arguments.RootElement.ValueKind != JsonValueKind.Array) { return (false, $"arguments must be a JSON array: {call.Arguments}"); }
                }
                var body = JsonSerializer.Serialize(new { contract = call.Contract, key = call.Key, method = call.Method, arguments = call.Arguments });
                var answer = await server.Run("neurons_call", body, ct);
                var passed = !answer.IsError && (call.Expected == "*" || answer.Text.Contains(call.Expected, StringComparison.Ordinal));
                return (passed, passed ? answer.Text : $"expected {call.Expected}; answered {answer.Text}");
            });
        }
        foreach (var span in plan.Spans)
        {
            yield return new($"span {span}", async ct =>
            {
                var count = await _cluster!.Spans($"candidate-{sha}", span, ct);
                return (count > 0, $"{count} span(s)");
            });
        }
    }

    private async Task<EngineerLiveProbe> ProbeHttpAsync(string probeName, string category, string url, CancellationToken ct)
    {
        if (_httpClientFactory is null && _httpClient is null)
        {
            return new EngineerLiveProbe(probeName, category, false, 503, $"No HttpClient or IHttpClientFactory configured to probe '{url}'; fail-closed policy refused.");
        }

        try
        {
            using var owned = _httpClientFactory?.CreateClient();
            owned?.Timeout = TimeSpan.FromSeconds(15);
            var client = owned ?? _httpClient!;
            using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return new EngineerLiveProbe(probeName, category, code is >= 200 and < 400, code, $"GET {url} -> HTTP {code}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Candidate HTTP probe failed for '{url}'.");
            return new EngineerLiveProbe(probeName, category, false, 503, $"Candidate HTTP probe failed for '{url}': {ex.Message}");
        }
    }

    private static void RecordProbe(
        EngineerLiveProbe probe,
        List<EngineerLiveProbe> probes,
        List<string> traces,
        string tracePrefix,
        ref int count4xx,
        ref int count5xx,
        ref int errorLogs)
    {
        probes.Add(probe);
        if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
        if (probe.HttpStatus is >= 500) { count5xx++; }
        if (!probe.Passed) { errorLogs++; }
        traces.Add($"{tracePrefix} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={probe.Passed}");
    }

    private static EngineerLiveReport FailReport(
        IReadOnlyList<EngineerLiveProbe> probes,
        IReadOnlyList<string> traces,
        string detail,
        int http4xx = 0,
        int http5xx = 0,
        int errorSpans = 1,
        int errorLogs = 1) => new(
            Succeeded: false,
            HostHealthy: false,
            McpProbesPassed: false,
            PlaywrightPassed: false,
            Http4xxCount: http4xx,
            Http5xxCount: http5xx,
            ErrorSpanCount: errorSpans,
            ErrorLogCount: errorLogs,
            Probes: [.. probes],
            TraceSummaries: [.. traces],
            Summary: $"LiveVerify=False · {detail}");

    internal static bool TryValidateRelativeHealthPath(string? rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            normalizedPath = "/health";
            return true;
        }

        if (!rawPath.StartsWith('/')
            || rawPath.StartsWith("//", StringComparison.Ordinal)
            || rawPath.Contains("://", StringComparison.Ordinal)
            || rawPath.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            error = $"Invalid candidate HealthPath '{rawPath}': must be a single-slash relative path without control characters, whitespace, or scheme.";
            return false;
        }

        normalizedPath = rawPath;
        return true;
    }

    private static IReadOnlyDictionary<string, string> ReadStagedTouchedFiles(string? worktreePath, IReadOnlyList<string>? touchedFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(worktreePath) || touchedFiles is not { Count: > 0 } || !Directory.Exists(worktreePath))
        {
            return result;
        }

        var fullRoot = Path.GetFullPath(worktreePath);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar)) { fullRoot += Path.DirectorySeparatorChar; }

        foreach (var rel in touchedFiles)
        {
            if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) { continue; }
            var candidatePath = Path.GetFullPath(Path.Combine(fullRoot, rel));
            if (!candidatePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidatePath)) { continue; }
            result[rel.Replace('\\', '/')] = File.ReadAllText(candidatePath);
        }

        return result;
    }

    internal static string NormalizeCandidateSha(string? commitSha, string worktreePath)
    {
        if (!string.IsNullOrWhiteSpace(commitSha))
        {
            var trimmed = commitSha.Trim().ToLowerInvariant();
            if (trimmed.Length >= 7 && trimmed.All(static c => char.IsAsciiHexDigit(c)))
            {
                return trimmed.Length > 12 ? trimmed[..12] : trimmed;
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(worktreePath ?? "candidate")))[..12];
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";
}
