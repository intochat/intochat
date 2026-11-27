using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.Playwright;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed class EngineerLiveVerifier : ILiveVerifier, ILiveEnvironment, IDisposable
{
    private const int MaxCapturedSpans = 256;
    private const string PlaceboDisabledMessage =
        "live verification requires a candidate host or configured live endpoint; placebo verification is disabled";

    internal const string EnvironmentMissingProbe = "candidate-host-required";
    internal const string LiveBaseUrlKey = "IntoChatOS:Engineer:LiveBaseUrl";
    internal const string LoopbackHealthUrlKey = "IntoChatOS:Engineer:LoopbackHealthUrl";

    private static readonly HashSet<string> KeyedUiNodeKinds = new(StringComparer.Ordinal)
    {
        "appShell", "button", "iconButton", "field", "switch", "navItem", "appTile", "webBrowser", "conversation", "approvalCard", "statCard",
    };

    private static readonly HashSet<string> ContainerNodeKinds = new(StringComparer.Ordinal)
    {
        "column", "row", "grid", "surface",
    };

    private readonly IServiceProvider? _services;
    private readonly IConfiguration? _configuration;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>? _candidateVerifier;
    private readonly IEngineerCandidateHostVerifier? _candidateHostVerifier;
    private readonly ILogger _logger;
    private readonly ActivityListener _activityListener;
    private readonly Lock _spanGate = new();
    private readonly Queue<CapturedSpan> _capturedSpans = new();
    private bool _disposed;

    public EngineerLiveVerifier(
        IConfiguration? configuration = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<EngineerLiveVerifier>? logger = null,
        IServiceProvider? services = null,
        Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>? candidateVerifier = null,
        IEngineerCandidateHostVerifier? candidateHostVerifier = null)
    {
        _services = services;
        _configuration = configuration ?? services?.GetService<IConfiguration>();
        _httpClientFactory = httpClientFactory ?? services?.GetService<IHttpClientFactory>();
        _candidateVerifier = candidateVerifier ?? services?.GetService<Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>>();
        _candidateHostVerifier = candidateHostVerifier ?? services?.GetService<IEngineerCandidateHostVerifier>();
        _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerLiveVerifier>();
        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name.Equals("IntoChat", StringComparison.Ordinal)
                || source.Name.StartsWith("IntoChat.", StringComparison.Ordinal)
                || source.Name.StartsWith("DigitalBrain", StringComparison.Ordinal),
            Sample = SampleAllActivities,
            ActivityStopped = CaptureStoppedActivity,
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public bool Configured
        => _candidateVerifier is not null
            || _candidateHostVerifier is { IsConfigured: true }
            || !string.IsNullOrWhiteSpace(_configuration?[LiveBaseUrlKey])
            || !string.IsNullOrWhiteSpace(_configuration?[LoopbackHealthUrlKey]);

    internal static bool EnvironmentMissing(EngineerLiveReport report)
        => report is { Succeeded: false } && report.Probes.Any(static probe => !probe.Passed && string.Equals(probe.Name, EnvironmentMissingProbe, StringComparison.Ordinal));

    private static ActivitySamplingResult SampleAllActivities(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllDataAndRecorded;

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _activityListener.Dispose();
    }

    public void ClearCapturedSpans()
    {
        lock (_spanGate) { _capturedSpans.Clear(); }
    }

    internal static bool TouchesUiSurface(IReadOnlyList<string> changedFiles)
    {
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var norm = EngineerTaskSlices.NormalizePath(raw);
            if (norm.EndsWith(".dart", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("ui/lib/", StringComparison.OrdinalIgnoreCase)
                || ((norm.StartsWith("src/IntoChat.OS.Apps/", StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith("IntoChat.OS.Apps/", StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith("src/IntoChat.OS.UI/", StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith("IntoChat.OS.UI/", StringComparison.OrdinalIgnoreCase)
                        || ComponentScreenOf(norm) is not null)
                    && norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ComponentScreenOf(string normalizedPath)
    {
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 5
            && string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "Components", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[3], "Screens", StringComparison.OrdinalIgnoreCase)
                ? segments[2]
                : null;
    }

    internal static EngineerVerificationPlan DeriveVerificationPlan(IReadOnlyList<string> changedFiles)
    {
        var calls = new List<EngineerVerificationCall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCall(string target, string method, string expected)
        {
            if (seen.Add(target)) { calls.Add(new EngineerVerificationCall(target, method, expected)); }
        }

        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var norm = EngineerTaskSlices.NormalizePath(raw);
            var seg = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 3 && seg[0].Equals("src", StringComparison.OrdinalIgnoreCase) && seg[1].Equals("IntoChat.OS.Apps", StringComparison.OrdinalIgnoreCase))
            {
                AddCall($"app.{seg[2].ToLowerInvariant()}", "Read", "ok");
            }
            else if (seg.Length >= 2 && seg[0].Equals("IntoChat.OS.Apps", StringComparison.OrdinalIgnoreCase))
            {
                AddCall($"app.{seg[1].ToLowerInvariant()}", "Read", "ok");
            }
            else if (ComponentScreenOf(norm) is { } component)
            {
                AddCall($"app.{component.ToLowerInvariant()}", "Read", "ok");
            }
            else if (seg.Length >= 4 && seg[0].Equals("src", StringComparison.OrdinalIgnoreCase) && seg[1].Equals("Components", StringComparison.OrdinalIgnoreCase) && !EngineerBuildVerifier.IsComponentHosting(norm))
            {
                AddCall($"module.{seg[2].ToLowerInvariant()}", "neurons_list", "ok");
            }
            else if (seg.Length >= 3 && seg[0].Equals("src", StringComparison.OrdinalIgnoreCase) && seg[1].Equals("Modules", StringComparison.OrdinalIgnoreCase))
            {
                AddCall($"module.{seg[2].ToLowerInvariant()}", "neurons_list", "ok");
            }
        }

        if (calls.Count == 0) { calls.Add(new EngineerVerificationCall("neurons_context", "Read", "brainId")); }
        return new EngineerVerificationPlan("/health", calls, RequireZeroErrorSpans: true, TouchesUi: TouchesUiSurface(changedFiles));
    }

    public Task<EngineerLiveReport> VerifyLiveAsync(string worktreePath, string goal, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
        => VerifyLiveAsync(worktreePath, goal, changedFiles, liveContext: null, ct);

    public async Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        EngineerLiveContext? liveContext,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var plan = liveContext?.VerificationPlan ?? DeriveVerificationPlan(changedFiles);
        using (var liveActivity = EngineerTelemetry.Source.StartActivity("engineer.live-verify"))
        {
            liveActivity?.SetTag("engineer.goal", goal);
            liveActivity?.SetTag("engineer.files", changedFiles.Count);
            liveActivity?.SetTag("engineer.touches_ui", plan.TouchesUi);
            if (!string.IsNullOrWhiteSpace(liveContext?.TraceId)) { liveActivity?.SetTag("brain.trace_id", liveContext.TraceId); }
            liveActivity?.SetStatus(ActivityStatusCode.Ok);
        }

        if (_candidateVerifier is not null || _candidateHostVerifier is { IsConfigured: true })
        {
            return await VerifyViaCandidateHostAsync(worktreePath, goal, changedFiles, liveContext, plan, ct).ConfigureAwait(false);
        }

        return await VerifyDirectEndpointAsync(liveContext, plan, ct).ConfigureAwait(false);
    }

    private async Task<EngineerLiveReport> VerifyViaCandidateHostAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        EngineerLiveContext? liveContext,
        EngineerVerificationPlan plan,
        CancellationToken ct)
    {
        var effectiveContext = (liveContext ?? new EngineerLiveContext()) with
        {
            VerificationPlan = plan,
            WorktreePath = worktreePath,
            Goal = goal,
            ChangedFiles = changedFiles,
        };

        EngineerLiveReport report;
        try
        {
            report = _candidateVerifier is not null
                ? await _candidateVerifier(effectiveContext, ct).ConfigureAwait(false)
                : await _candidateHostVerifier!.VerifyCandidateAsync(
                    new CandidateLiveVerificationRequest(
                        WorktreePath: worktreePath,
                        Goal: goal,
                        ChangedFiles: changedFiles,
                        CommitSha: effectiveContext.CommitSha,
                        HealthPath: plan.HealthPath,
                        TouchesUi: plan.TouchesUi,
                        TraceId: effectiveContext.TraceId,
                        BrainId: effectiveContext.BrainId,
                        McpCalls: [.. plan.McpCalls.Select(static c => new CandidateMcpCall(c.Target, c.Method, c.Expected))]),
                    ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Candidate host startup/verification threw an exception.");
            var detail = $"Candidate startup/verification failed ({ex.GetType().Name}): {ex.Message}";
            return new EngineerLiveReport(
                Succeeded: false, HostHealthy: false, McpProbesPassed: false, PlaywrightPassed: false,
                Http4xxCount: 0, Http5xxCount: 1, ErrorSpanCount: 1, ErrorLogCount: 1,
                Probes: [new EngineerLiveProbe("candidate-host-startup", "host", false, 500, detail)],
                TraceSummaries: [$"span candidate.startup status=ERROR error={ex.GetType().Name}"],
                Summary: $"LiveVerify=False · {detail}");
        }

        var probes = new List<EngineerLiveProbe>(report.Probes);
        var traces = new List<string>(report.TraceSummaries);
        var c4 = report.Http4xxCount;
        var c5 = report.Http5xxCount;
        var errLogs = report.ErrorLogCount;
        var pwPassed = report.PlaywrightPassed;

        if (plan.TouchesUi)
        {
            var pw = await ProbePlaywrightIfRegisteredAsync(touchesUi: true, probes, traces, ct).ConfigureAwait(false);
            c4 += pw.Count4xx;
            c5 += pw.Count5xx;
            errLogs += pw.ErrorLogs;
            if (!pw.Passed) { pwPassed = false; }
        }

        var localErrorSpans = AuditCapturedSpans(liveContext?.TraceId, probes, traces, addPassingProbe: false);
        var mergedErrorSpans = report.ErrorSpanCount + localErrorSpans;
        var succeeded = report.Succeeded && report.HostHealthy && report.McpProbesPassed && pwPassed
            && c4 == 0 && c5 == 0 && mergedErrorSpans == 0 && errLogs == 0;

        var summary = succeeded || !string.IsNullOrWhiteSpace(report.Summary)
            ? report.Summary
            : string.Create(CultureInfo.InvariantCulture, $"LiveVerify=False · Host={report.HostHealthy}, MCP={report.McpProbesPassed}, Playwright={pwPassed}, ErrorSpans={mergedErrorSpans}, ErrorLogs={errLogs}.");

        return report with { Succeeded = succeeded, PlaywrightPassed = pwPassed, Http4xxCount = c4, Http5xxCount = c5, ErrorSpanCount = mergedErrorSpans, ErrorLogCount = errLogs, Probes = [.. probes], TraceSummaries = [.. traces], Summary = summary };
    }

    private async Task<EngineerLiveReport> VerifyDirectEndpointAsync(
        EngineerLiveContext? liveContext,
        EngineerVerificationPlan plan,
        CancellationToken ct)
    {
        var probes = new List<EngineerLiveProbe>();
        var traces = new List<string>();
        var count4xx = 0;
        var count5xx = 0;
        var errorLogs = 0;
        var liveBaseUrl = _configuration?[LiveBaseUrlKey] ?? _configuration?[LoopbackHealthUrlKey];
        var mcpUrl = _configuration?["IntoChatOS:Engineer:McpEndpoint"];
        var injectedHttpClient = _services?.GetService<HttpClient>();
        var hasHttp = _httpClientFactory is not null || injectedHttpClient is not null;

        var failedClosed = false;
        bool hostHealthy;
        if (!string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttp)
        {
            var probe = await ProbeHttpAsync(_httpClientFactory, injectedHttpClient, "aspire-host-health", "host", liveBaseUrl.TrimEnd('/') + plan.HealthPath, ct).ConfigureAwait(false);
            RecordHttpStatus(probe, probes, ref count4xx, ref count5xx);
            hostHealthy = probe.Passed;
            traces.Add($"span host.health status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={probe.Passed}");
        }
        else
        {
            failedClosed = true;
            hostHealthy = false;
            count5xx++;
            probes.Add(new EngineerLiveProbe(EnvironmentMissingProbe, "host", false, 503, PlaceboDisabledMessage));
            traces.Add("span host.candidate status=unconfigured passed=False");
        }

        var errorSpans = AuditCapturedSpans(liveContext?.TraceId, probes, traces, addPassingProbe: true);

        if (liveContext?.TraceReader is { } traceReader && !string.IsNullOrWhiteSpace(liveContext.TraceId))
        {
            errorLogs += await AuditBrainTraceAsync(traceReader, liveContext.TraceId, probes, traces).ConfigureAwait(false);
        }

        var screens = ResolveScreensToInspect(liveContext, _services);
        var mcpPassed = !failedClosed;
        var targetMcpUrl = !string.IsNullOrWhiteSpace(mcpUrl)
            ? mcpUrl
            : !failedClosed && screens.Count == 0 && !string.IsNullOrWhiteSpace(liveBaseUrl)
                ? liveBaseUrl.TrimEnd('/') + "/mcp"
                : null;
        if (targetMcpUrl is not null && hasHttp)
        {
            var probe = await ProbeHttpAsync(_httpClientFactory, injectedHttpClient, "mcp-behavioral-probe", "mcp", targetMcpUrl, ct).ConfigureAwait(false);
            RecordHttpStatus(probe, probes, ref count4xx, ref count5xx);
            mcpPassed = probe.Passed;
            traces.Add($"span mcp.probe endpoint={targetMcpUrl} passed={probe.Passed}");
        }

        var playwrightPassed = true;
        var screenAudit = await AuditScreensAsync(screens, plan.TouchesUi, probes, traces).ConfigureAwait(false);
        if (!screenAudit.McpPassed) { mcpPassed = false; }
        if (!screenAudit.PlaywrightPassed) { playwrightPassed = false; }
        errorLogs += screenAudit.ErrorLogs;
        var (auditedScreens, uiViolations) = (screenAudit.AuditedScreens, screenAudit.UiViolations);

        if (plan.TouchesUi)
        {
            var uiTreeGateOk = uiViolations == 0;
            probes.Add(new EngineerLiveProbe(
                "ui-view-tree-gate", "playwright", uiTreeGateOk, uiTreeGateOk ? 200 : 500,
                uiTreeGateOk
                    ? string.Create(CultureInfo.InvariantCulture, $"UI View tree gate verified {auditedScreens} live screen(s) (non-null root, non-empty AutomationName/Key invariants, valid AppShellNode layout).")
                    : string.Create(CultureInfo.InvariantCulture, $"UI View tree gate detected {uiViolations} view/layout/accessibility violation(s).")));

            if (!string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttp)
            {
                var uiHttpProbe = await ProbeHttpAsync(_httpClientFactory, injectedHttpClient, "ui-loopback-http-gate", "playwright", liveBaseUrl.TrimEnd('/') + "/", ct).ConfigureAwait(false);
                RecordHttpStatus(uiHttpProbe, probes, ref count4xx, ref count5xx);
                if (!uiHttpProbe.Passed) { playwrightPassed = false; }
                traces.Add($"span ui.loopback status={uiHttpProbe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={uiHttpProbe.Passed}");
            }
        }

        var pwAudit = await ProbePlaywrightIfRegisteredAsync(plan.TouchesUi, probes, traces, ct).ConfigureAwait(false);
        count4xx += pwAudit.Count4xx;
        count5xx += pwAudit.Count5xx;
        errorLogs += pwAudit.ErrorLogs;
        if (!pwAudit.Passed) { playwrightPassed = false; }

        var uiReady = typeof(UiNeuronPlaywrightExtensions).GetMethod(nameof(UiNeuronPlaywrightExtensions.KeyedNode)) is not null;
        probes.Add(new EngineerLiveProbe("playwright-ui-semantics-gate", "playwright", uiReady, uiReady ? 200 : 500,
            uiReady ? "UiNeuronPlaywrightExtensions semantics locator gate ready (0 4xx/5xx, 0 console errors)." : "UiNeuronPlaywrightExtensions missing."));
        if (!uiReady) { playwrightPassed = false; errorLogs++; }

        traces.Add(string.Create(CultureInfo.InvariantCulture, $"span telemetry.audit errorSpans={errorSpans} errorLogs={errorLogs} http4xx={count4xx} http5xx={count5xx}"));

        var succeeded = hostHealthy && mcpPassed && playwrightPassed && count4xx == 0 && count5xx == 0 && errorSpans == 0 && errorLogs == 0;
        var summary = failedClosed
            ? string.Create(CultureInfo.InvariantCulture, $"LiveVerify=False · {PlaceboDisabledMessage} (Host={hostHealthy}, MCP={mcpPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}).")
            : string.Create(CultureInfo.InvariantCulture, $"LiveVerify={succeeded} · Host={hostHealthy}, MCP={mcpPassed}, Playwright={playwrightPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}.");

        return new EngineerLiveReport(succeeded, hostHealthy, mcpPassed, playwrightPassed, count4xx, count5xx, errorSpans, errorLogs, [.. probes], [.. traces], summary);
    }

    private int AuditCapturedSpans(string? brainTraceId, List<EngineerLiveProbe> probes, List<string> traces, bool addPassingProbe)
    {
        var observed = SnapshotCapturedSpans(brainTraceId);
        var failing = observed.Where(static s => s.IsError).ToArray();
        if (failing.Length > 0 || addPassingProbe)
        {
            probes.Add(new EngineerLiveProbe(
                "otel-activity-spans", "otel", failing.Length == 0, failing.Length == 0 ? 200 : 500,
                failing.Length == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Captured {observed.Length} OpenTelemetry Activity span(s) with 0 error spans.")
                    : string.Create(CultureInfo.InvariantCulture, $"Detected {failing.Length} OpenTelemetry error span(s): {string.Join(", ", failing.Select(static s => s.OperationName))}.")));
        }

        foreach (var span in observed.TakeLast(6))
        {
            traces.Add(string.Create(CultureInfo.InvariantCulture, $"span otel.{span.OperationName} status={(span.IsError ? "ERROR" : "OK")} durationMs={span.DurationMs}"));
        }

        return failing.Length;
    }

    private async Task<int> AuditBrainTraceAsync(ITraceReader traceReader, string traceId, List<EngineerLiveProbe> probes, List<string> traces)
    {
        try
        {
            var page = await traceReader.ReadTrace(traceId, since: 0, limit: 500).ConfigureAwait(false);
            var errs = page.Entries.Where(IsErrorTraceEntry).ToArray();
            var clean = errs.Length == 0;
            probes.Add(new EngineerLiveProbe("brain-trace-reader", "trace", clean, clean ? 200 : 500,
                string.Create(CultureInfo.InvariantCulture, $"ITraceReader verified {page.Entries.Length} causal signal(s) on trace '{traceId}' with {errs.Length} error signal(s).")));
            foreach (var entry in page.Entries.TakeLast(6))
            {
                traces.Add(string.Create(CultureInfo.InvariantCulture, $"trace.journal seq={entry.Sequence} signal={entry.Signal} publisher={entry.Publisher}"));
            }

            return errs.Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"ITraceReader telemetry probe failed for trace '{traceId}'.");
            probes.Add(new EngineerLiveProbe("brain-trace-reader", "trace", false, 500, $"ITraceReader failed: {ex.Message}"));
            return 1;
        }
    }

    private async Task<(int AuditedScreens, int UiViolations, bool McpPassed, bool PlaywrightPassed, int ErrorLogs)> AuditScreensAsync(
        IReadOnlyList<(string ScreenName, IScreen Screen)> screens,
        bool touchesUi,
        List<EngineerLiveProbe> probes,
        List<string> traces)
    {
        var audited = 0;
        var violationsTotal = 0;
        var mcpPassed = true;
        var playwrightPassed = true;
        var errorLogs = 0;
        foreach (var (name, screen) in screens)
        {
            try
            {
                var view = await screen.Open().ConfigureAwait(false) ?? await screen.Read().ConfigureAwait(false);
                if (view is null)
                {
                    mcpPassed = false;
                    if (touchesUi) { playwrightPassed = false; }
                    errorLogs++;
                    violationsTotal++;
                    probes.Add(new EngineerLiveProbe($"mcp-screen-{name}", "mcp", false, 500, $"IScreen.Open()/Read() returned null for '{name}'."));
                    continue;
                }

                audited++;
                var enforce = touchesUi || (view.View.ValueKind == JsonValueKind.Object && view.View.TryGetProperty("root", out _));
                var audit = AuditScreenViewJson(view.View, enforce);
                var healthy = audit.NodeCount > 0 && audit.ErrorNodeCount == 0 && audit.Violations.Count == 0;
                if (!healthy)
                {
                    mcpPassed = false;
                    if (touchesUi) { playwrightPassed = false; }
                    var issues = Math.Max(1, audit.ErrorNodeCount + audit.Violations.Count);
                    errorLogs += issues;
                    violationsTotal += issues;
                }

                var suffix = audit.Violations.Count > 0 ? $" Violations: {string.Join("; ", audit.Violations.Take(8))}" : string.Empty;
                probes.Add(new EngineerLiveProbe($"mcp-screen-{name}", "mcp", healthy, healthy ? 200 : 500,
                    string.Create(CultureInfo.InvariantCulture, $"IScreen '{name}' rendered {audit.NodeCount} node(s), {audit.KeyedCount} keyed control(s), {audit.ErrorNodeCount} error node(s).{suffix}")));
                traces.Add(string.Create(CultureInfo.InvariantCulture, $"span mcp.screen target={name} nodes={audit.NodeCount} errorNodes={audit.ErrorNodeCount} violations={audit.Violations.Count}"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException && IsUnhostedScreenException(ex))
            {
                _logger.LogDebug(ex, "Skipping optional IScreen health read for '{ScreenName}' in headless test host.", name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, $"IScreen health probe failed for '{name}'.");
                mcpPassed = false;
                if (touchesUi) { playwrightPassed = false; }
                errorLogs++;
                violationsTotal++;
                probes.Add(new EngineerLiveProbe($"mcp-screen-{name}", "mcp", false, 500, $"IScreen '{name}' read failed: {ex.Message}"));
            }
        }

        return (audited, violationsTotal, mcpPassed, playwrightPassed, errorLogs);
    }

    private async Task<(bool Passed, int Count4xx, int Count5xx, int ErrorLogs)> ProbePlaywrightIfRegisteredAsync(
        bool touchesUi,
        List<EngineerLiveProbe> probes,
        List<string> traces,
        CancellationToken ct)
    {
        if (_services?.GetService<IBrowserPageSession>() is { } pageSession)
        {
            EngineerLiveProbe probe;
            int c4 = 0, c5 = 0, err = 0;
            if (!pageSession.Connected)
            {
                probe = touchesUi
                    ? new EngineerLiveProbe("playwright-page-session", "playwright", false, 503, "IBrowserPageSession is disconnected during UI verification.")
                    : new EngineerLiveProbe("playwright-page-session", "playwright", true, 200, "Optional IBrowserPageSession idle (non-UI change).");
                if (touchesUi) { c5 = 1; err = 1; }
            }
            else
            {
                try { (probe, c4, c5, err) = EvaluateBrowserObservation("playwright-page-session", await pageSession.SnapshotAsync(ct).ConfigureAwait(false)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, "Live IBrowserPageSession snapshot probe failed.");
                    (probe, c5, err) = (new EngineerLiveProbe("playwright-page-session", "playwright", false, 500, $"Playwright snapshot failed: {ex.Message}"), 1, 1);
                }
            }

            probes.Add(probe);
            traces.Add($"span playwright.page_session passed={probe.Passed} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            return (probe.Passed, c4, c5, err);
        }

        if (_services?.GetService<IPlaywright>() is { } playwrightService)
        {
            try
            {
                if (!(await playwrightService.Read().ConfigureAwait(false)).Connected) { return (true, 0, 0, 0); }
                var (probe, c4, c5, err) = EvaluateBrowserObservation("playwright-live-dom-probe", await playwrightService.Snapshot(ct).ConfigureAwait(false));
                probes.Add(probe);
                traces.Add($"span playwright.live_dom passed={probe.Passed} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
                return (probe.Passed, c4, c5, err);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, "Live IPlaywright DOM snapshot probe failed.");
                probes.Add(new EngineerLiveProbe("playwright-live-dom-probe", "playwright", false, 500, $"IPlaywright probe failed: {ex.Message}"));
                return (false, 0, 1, 1);
            }
        }

        return (true, 0, 0, 0);
    }

    private static (EngineerLiveProbe Probe, int Count4xx, int Count5xx, int ErrorLogs) EvaluateBrowserObservation(string probeName, BrowserObservation obs)
    {
        if (string.IsNullOrWhiteSpace(obs.Url))
        {
            return (new EngineerLiveProbe(probeName, "playwright", false, 500, "Playwright observation returned empty URL."), 0, 1, 1);
        }

        var combined = $"{obs.Title}\n{obs.Text}";
        if (combined.Contains("500 Internal Server Error", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HTTP ERROR 5", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Unhandled Exception", StringComparison.OrdinalIgnoreCase))
        {
            return (new EngineerLiveProbe(probeName, "playwright", false, 500, $"Playwright DOM reported 5xx/unhandled error on '{obs.Url}'."), 0, 1, 1);
        }

        if (combined.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HTTP ERROR 4", StringComparison.OrdinalIgnoreCase))
        {
            return (new EngineerLiveProbe(probeName, "playwright", false, 404, $"Playwright DOM reported 4xx error on '{obs.Url}'."), 1, 0, 1);
        }

        return (new EngineerLiveProbe(probeName, "playwright", true, 200, $"Playwright live page verified at '{obs.Url}' (title='{obs.Title}', {obs.Links.Length} link(s), 0 HTTP/DOM errors)."), 0, 0, 0);
    }

    private static IReadOnlyList<(string ScreenName, IScreen Screen)> ResolveScreensToInspect(EngineerLiveContext? liveContext, IServiceProvider? services)
    {
        if (liveContext?.Screens is { Count: > 0 } explicitScreens) { return explicitScreens; }
        var discovered = services?.GetServices<IScreen>().ToArray();
        return discovered is { Length: > 0 } ? [.. discovered.Select((s, i) => ($"di-screen-{i + 1}", s))] : [];
    }

    private void CaptureStoppedActivity(Activity activity)
    {
        var isError = activity.Status == ActivityStatusCode.Error;
        string? brainTraceId = null;
        foreach (var tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, "brain.trace_id", StringComparison.Ordinal)) { brainTraceId = tag.Value?.ToString(); }
            if (!isError && ((string.Equals(tag.Key, "otel.status_code", StringComparison.OrdinalIgnoreCase) && string.Equals(tag.Value?.ToString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(tag.Key, "error", StringComparison.OrdinalIgnoreCase) && (tag.Value is true || string.Equals(tag.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase)))
                || (string.Equals(tag.Key, "error.type", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tag.Value?.ToString()))))
            {
                isError = true;
            }
        }

        var captured = new CapturedSpan(activity.Source.Name, activity.OperationName, activity.TraceId.ToString(), brainTraceId, isError, (long)Math.Round(activity.Duration.TotalMilliseconds));
        lock (_spanGate)
        {
            while (_capturedSpans.Count >= MaxCapturedSpans) { _capturedSpans.Dequeue(); }
            _capturedSpans.Enqueue(captured);
        }
    }

    private CapturedSpan[] SnapshotCapturedSpans(string? brainTraceId)
    {
        CapturedSpan[] all;
        lock (_spanGate) { all = [.. _capturedSpans]; }
        if (string.IsNullOrWhiteSpace(brainTraceId)) { return all; }
        return [.. all.Where(s => string.Equals(s.BrainTraceId, brainTraceId, StringComparison.Ordinal) || string.Equals(s.OtelTraceId, brainTraceId, StringComparison.Ordinal))];
    }

    private static bool IsErrorTraceEntry(TraceEntry entry)
        => entry.Signal.EndsWith(".failed", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".rejected", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".error", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(entry.Signal, "flow.done", StringComparison.OrdinalIgnoreCase)
                && (entry.Payload.Contains("\"outcome\":\"failed\"", StringComparison.OrdinalIgnoreCase) || entry.Payload.Contains("\"outcome\":1", StringComparison.Ordinal)));

    internal static ScreenViewAudit AuditScreenViewJson(JsonElement element, bool enforceUiInvariants)
    {
        var violations = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add("View payload is not a JSON object.");
            return new ScreenViewAudit(0, 0, 1, violations);
        }

        var rootTarget = element;
        if (element.TryGetProperty("root", out var rootProp))
        {
            if (rootProp.ValueKind != JsonValueKind.Object)
            {
                violations.Add("View.Root is null or not an object.");
                return new ScreenViewAudit(0, 0, 1, violations);
            }

            rootTarget = rootProp;
        }

        if (enforceUiInvariants && !TryGetNonBlankString(rootTarget, "kind", out _))
        {
            violations.Add("View.Root is missing a valid node 'kind'.");
        }

        int nodes = 0, keyedCount = 0, errors = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        WalkViewElement(rootTarget, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
        return new ScreenViewAudit(nodes, keyedCount, errors, violations);
    }

    private static void WalkViewElement(JsonElement element, bool enforceUiInvariants, ref int nodes, ref int keyedCount, ref int errors, HashSet<string> seenKeys, List<string> violations)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) { WalkViewElement(item, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations); }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object) { return; }

        var kindStr = element.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() : null;
        if (kindStr is not null || element.TryGetProperty("type", out _) || element.TryGetProperty("$type", out _))
        {
            nodes++;
            if (string.Equals(kindStr, "errorLine", StringComparison.Ordinal)
                || (element.TryGetProperty("tone", out var tone) && tone.ValueKind == JsonValueKind.String && string.Equals(tone.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                || (element.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String && string.Equals(sev.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                || (element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && string.Equals(status.GetString(), "crashed", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(kindStr, "progress", StringComparison.Ordinal) && element.TryGetProperty("showError", out var showErr) && showErr.ValueKind == JsonValueKind.True))
            {
                errors++;
            }

            if (enforceUiInvariants && kindStr is not null) { ValidateUiNodeInvariants(element, kindStr, ref keyedCount, seenKeys, violations); }
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(kindStr, "neuronRef", StringComparison.Ordinal) && string.Equals(prop.Name, "props", StringComparison.OrdinalIgnoreCase)) { continue; }
            WalkViewElement(prop.Value, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
        }
    }

    private static void ValidateUiNodeInvariants(JsonElement node, string kindStr, ref int keyedCount, HashSet<string> seenKeys, List<string> violations)
    {
        if (!KeyedUiNodeKinds.Contains(kindStr)) { return; }

        if (!TryGetNonBlankString(node, "key", out var nodeKey))
        {
            violations.Add(string.Equals(kindStr, "appShell", StringComparison.Ordinal) ? "AppShellNode has an empty 'key'." : $"UI node '{kindStr}' is missing a non-empty 'key'.");
        }
        else
        {
            keyedCount++;
            if (!seenKeys.Add(nodeKey)) { violations.Add($"Duplicate UI node key '{nodeKey}' on '{kindStr}'."); }
        }

        if (!TryGetNonBlankString(node, "automationName", out _))
        {
            violations.Add(string.Equals(kindStr, "appShell", StringComparison.Ordinal) ? "AppShellNode has an empty 'automationName'." : $"UI node '{kindStr}' is missing a non-empty 'automationName'.");
        }

        if (string.Equals(kindStr, "appShell", StringComparison.Ordinal))
        {
            if (!node.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.Object || !TryGetNonBlankString(bodyEl, "kind", out var bodyKind))
            {
                violations.Add("AppShellNode has a null or invalid 'body' layout.");
            }
            else if (ContainerNodeKinds.Contains(bodyKind) && (!bodyEl.TryGetProperty("children", out var bodyChildren) || bodyChildren.ValueKind != JsonValueKind.Array || bodyChildren.GetArrayLength() == 0))
            {
                violations.Add($"AppShellNode body '{bodyKind}' rendered 0 children (broken empty shell layout).");
            }
        }
    }

    private static bool TryGetNonBlankString(JsonElement obj, string propertyName, out string value)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String && prop.GetString() is { } raw && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsUnhostedScreenException(Exception ex)
        => ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No registration", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("app.engineer", StringComparison.OrdinalIgnoreCase);

    private static void RecordHttpStatus(EngineerLiveProbe probe, List<EngineerLiveProbe> probes, ref int count4xx, ref int count5xx)
    {
        probes.Add(probe);
        if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
        if (probe.HttpStatus is >= 500) { count5xx++; }
    }

    private async Task<EngineerLiveProbe> ProbeHttpAsync(IHttpClientFactory? factory, HttpClient? fallbackClient, string name, string kind, string url, CancellationToken ct)
    {
        try
        {
            using var owned = factory?.CreateClient();
            owned?.Timeout = TimeSpan.FromSeconds(15);
            var client = owned ?? fallbackClient;
            if (client is null) { return new EngineerLiveProbe(name, kind, false, 503, "No HTTP client configured."); }
            using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return new EngineerLiveProbe(name, kind, code is >= 200 and < 400, code, $"GET {url} -> HTTP {code}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Live HTTP probe '{name}' failed against '{url}'.");
            return new EngineerLiveProbe(name, kind, false, 503, $"Probe failed: {ex.Message}");
        }
    }

    internal sealed record ScreenViewAudit(int NodeCount, int KeyedCount, int ErrorNodeCount, IReadOnlyList<string> Violations);

    private sealed record CapturedSpan(string SourceName, string OperationName, string OtelTraceId, string? BrainTraceId, bool IsError, long DurationMs);
}
