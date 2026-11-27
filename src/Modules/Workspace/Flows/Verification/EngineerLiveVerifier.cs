using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed partial class EngineerLiveVerifier : ILiveVerifier, ILiveEnvironment, IDisposable
{
    private const int MaxCapturedSpans = 256;

    private static readonly HashSet<string> KeyedUiNodeKinds = new(StringComparer.Ordinal)
    {
        "appShell",
        "button",
        "iconButton",
        "field",
        "switch",
        "navItem",
        "appTile",
        "webBrowser",
        "conversation",
        "approvalCard",
        "statCard",
    };

    private static readonly HashSet<string> ContainerNodeKinds = new(StringComparer.Ordinal)
    {
        "column",
        "row",
        "grid",
        "surface",
    };

    private const string PlaceboDisabledMessage =
        "live verification requires a candidate host or configured live endpoint; placebo verification is disabled";

    // The probe a report carries when there was nothing to verify against: no candidate host and no configured live endpoint.
    internal const string EnvironmentMissingProbe = "candidate-host-required";

    // Whether the report says the change could not be checked at all, as opposed to checked and found wanting.
    internal static bool EnvironmentMissing(EngineerLiveReport report)
        => report is { Succeeded: false } && report.Probes.Any(static probe => !probe.Passed && string.Equals(probe.Name, EnvironmentMissingProbe, StringComparison.Ordinal));

    internal const string LiveBaseUrlKey = "IntoChatOS:Engineer:LiveBaseUrl";
    internal const string LoopbackHealthUrlKey = "IntoChatOS:Engineer:LoopbackHealthUrl";

    private readonly IServiceProvider? _services;
    private readonly IConfiguration? _configuration;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>? _candidateVerifier;
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
        Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>? candidateVerifier = null)
    {
        _services = services;
        _configuration = configuration ?? services?.GetService<IConfiguration>();
        _httpClientFactory = httpClientFactory ?? services?.GetService<IHttpClientFactory>();
        _candidateVerifier = candidateVerifier ?? services?.GetService<Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>>();
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

    // A live environment is configured when a candidate verifier is registered in the services, or a live endpoint is set in
    // configuration. Whether it answers is another matter: that is found by running the check.
    public bool Configured
        => _candidateVerifier is not null
            || !string.IsNullOrWhiteSpace(_configuration?[LiveBaseUrlKey])
            || !string.IsNullOrWhiteSpace(_configuration?[LoopbackHealthUrlKey]);

    private static ActivitySamplingResult SampleAllActivities(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllDataAndRecorded;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activityListener.Dispose();
    }

    public void ClearCapturedSpans()
    {
        lock (_spanGate)
        {
            _capturedSpans.Clear();
        }
    }

    internal static bool TouchesUiSurface(IReadOnlyList<string> changedFiles)
    {
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

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

    // src/Components/<Name>/Screens/** is a component's screen: the component name, else null.
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
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var norm = EngineerTaskSlices.NormalizePath(raw);
            var segments = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3
                && string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "IntoChat.OS.Apps", StringComparison.OrdinalIgnoreCase))
            {
                var target = $"app.{segments[2].ToLowerInvariant()}";
                if (seenTargets.Add(target))
                {
                    calls.Add(new EngineerVerificationCall(target, "Read", "ok"));
                }
            }
            else if (segments.Length >= 2
                && string.Equals(segments[0], "IntoChat.OS.Apps", StringComparison.OrdinalIgnoreCase))
            {
                var target = $"app.{segments[1].ToLowerInvariant()}";
                if (seenTargets.Add(target))
                {
                    calls.Add(new EngineerVerificationCall(target, "Read", "ok"));
                }
            }
            else if (ComponentScreenOf(norm) is { } component)
            {
                // Paths only: the app is named after its component folder (OWED.md: a component whose app name differs).
                var target = $"app.{component.ToLowerInvariant()}";
                if (seenTargets.Add(target))
                {
                    calls.Add(new EngineerVerificationCall(target, "Read", "ok"));
                }
            }
            else if (segments.Length >= 4
                && string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "Components", StringComparison.OrdinalIgnoreCase)
                && !EngineerBuildVerifier.IsComponentHosting(norm))
            {
                // Same rule as src/Modules/<Name>: the module id is the folder lowercased (Aspire -> "aspire").
                // Hosting/** is AppHost code and plans what an AppHost change plans: no call of its own.
                var target = $"module.{segments[2].ToLowerInvariant()}";
                if (seenTargets.Add(target))
                {
                    calls.Add(new EngineerVerificationCall(target, "neurons_list", "ok"));
                }
            }
            else if (segments.Length >= 3
                && string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "Modules", StringComparison.OrdinalIgnoreCase))
            {
                var target = $"module.{segments[2].ToLowerInvariant()}";
                if (seenTargets.Add(target))
                {
                    calls.Add(new EngineerVerificationCall(target, "neurons_list", "ok"));
                }
            }
        }

        if (calls.Count == 0)
        {
            calls.Add(new EngineerVerificationCall("neurons_context", "Read", "brainId"));
        }

        return new EngineerVerificationPlan(
            HealthPath: "/health",
            McpCalls: calls,
            RequireZeroErrorSpans: true,
            TouchesUi: TouchesUiSurface(changedFiles));
    }

    public Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
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

        var probes = new List<EngineerLiveProbe>();
        var traces = new List<string>();
        var count4xx = 0;
        var count5xx = 0;
        var errorSpans = 0;
        var errorLogs = 0;
        var plan = liveContext?.VerificationPlan ?? DeriveVerificationPlan(changedFiles);
        var touchesUi = plan.TouchesUi;

        using (var liveActivity = EngineerTelemetry.Source.StartActivity("engineer.live-verify"))
        {
            liveActivity?.SetTag("engineer.goal", goal);
            liveActivity?.SetTag("engineer.files", changedFiles.Count);
            liveActivity?.SetTag("engineer.touches_ui", touchesUi);
            if (!string.IsNullOrWhiteSpace(liveContext?.TraceId))
            {
                liveActivity?.SetTag("brain.trace_id", liveContext.TraceId);
            }

            liveActivity?.SetStatus(ActivityStatusCode.Ok);
        }

        // Candidate verification path: run the real candidate verifier callback when provided.
        if (_candidateVerifier is not null)
        {
            var effectiveContext = (liveContext ?? new EngineerLiveContext()) with
            {
                VerificationPlan = plan,
                WorktreePath = worktreePath,
                Goal = goal,
                ChangedFiles = changedFiles,
            };

            EngineerLiveReport candidateReport;
            try
            {
                candidateReport = await _candidateVerifier(effectiveContext, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, "Candidate host startup/verification threw an exception.");
                var detail = $"Candidate startup/verification failed ({ex.GetType().Name}): {ex.Message}";
                return new EngineerLiveReport(
                    Succeeded: false,
                    HostHealthy: false,
                    McpProbesPassed: false,
                    PlaywrightPassed: false,
                    Http4xxCount: 0,
                    Http5xxCount: 1,
                    ErrorSpanCount: 1,
                    ErrorLogCount: 1,
                    Probes: [new EngineerLiveProbe("candidate-host-startup", "host", false, 500, detail)],
                    TraceSummaries: [$"span candidate.startup status=ERROR error={ex.GetType().Name}"],
                    Summary: $"LiveVerify=False · {detail}");
            }

            var candidateObservedSpans = SnapshotCapturedSpans(liveContext?.TraceId);
            var candidateFailingSpans = candidateObservedSpans.Where(static s => s.IsError).ToArray();
            var mergedErrorSpans = candidateReport.ErrorSpanCount + candidateFailingSpans.Length;
            var mergedProbes = new List<EngineerLiveProbe>(candidateReport.Probes);
            var mergedTraces = new List<string>(candidateReport.TraceSummaries);
            if (candidateFailingSpans.Length > 0)
            {
                mergedProbes.Add(new EngineerLiveProbe(
                    "otel-activity-spans",
                    "otel",
                    false,
                    500,
                    string.Create(CultureInfo.InvariantCulture, $"Detected {candidateFailingSpans.Length} OpenTelemetry error span(s): {string.Join(", ", candidateFailingSpans.Select(static s => s.OperationName))}.")));
            }

            foreach (var span in candidateObservedSpans.TakeLast(6))
            {
                mergedTraces.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"span otel.{span.OperationName} status={(span.IsError ? "ERROR" : "OK")} durationMs={span.DurationMs}"));
            }

            var candidateSucceeded = candidateReport.Succeeded
                && candidateReport.HostHealthy
                && candidateReport.McpProbesPassed
                && candidateReport.PlaywrightPassed
                && candidateReport.Http4xxCount == 0
                && candidateReport.Http5xxCount == 0
                && mergedErrorSpans == 0
                && candidateReport.ErrorLogCount == 0;

            var candidateSummary = candidateSucceeded
                ? candidateReport.Summary
                : string.IsNullOrWhiteSpace(candidateReport.Summary)
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"LiveVerify=False · Host={candidateReport.HostHealthy}, MCP={candidateReport.McpProbesPassed}, ErrorSpans={mergedErrorSpans}, ErrorLogs={candidateReport.ErrorLogCount}.")
                    : candidateReport.Summary;

            return candidateReport with
            {
                Succeeded = candidateSucceeded,
                ErrorSpanCount = mergedErrorSpans,
                Probes = [.. mergedProbes],
                TraceSummaries = [.. mergedTraces],
                Summary = candidateSummary,
            };
        }

        var liveBaseUrl = _configuration?[LiveBaseUrlKey]
            ?? _configuration?[LoopbackHealthUrlKey];
        var mcpUrl = _configuration?["IntoChatOS:Engineer:McpEndpoint"];
        var injectedHttpClient = _services?.GetService<HttpClient>();
        var hasHttpTransport = _httpClientFactory is not null || injectedHttpClient is not null;

        var hostHealthy = false;
        var failedClosed = false;
        if (!string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttpTransport)
        {
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "aspire-host-health", "host", liveBaseUrl.TrimEnd('/') + plan.HealthPath, ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            hostHealthy = probe.Passed;
            traces.Add($"span host.health status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={probe.Passed}");
        }
        else
        {
            failedClosed = true;
            hostHealthy = false;
            count5xx++;
            probes.Add(new EngineerLiveProbe(
                EnvironmentMissingProbe,
                "host",
                false,
                503,
                PlaceboDisabledMessage));
            traces.Add("span host.candidate status=unconfigured passed=False");
        }

        // OpenTelemetry ActivityListener structured span audit (error spans, status codes, durations).
        var observedSpans = SnapshotCapturedSpans(liveContext?.TraceId);
        var failingSpans = observedSpans.Where(static s => s.IsError).ToArray();
        errorSpans += failingSpans.Length;
        probes.Add(new EngineerLiveProbe(
            "otel-activity-spans",
            "otel",
            failingSpans.Length == 0,
            failingSpans.Length == 0 ? 200 : 500,
            failingSpans.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Captured {observedSpans.Length} OpenTelemetry Activity span(s) with 0 error spans.")
                : string.Create(CultureInfo.InvariantCulture, $"Detected {failingSpans.Length} OpenTelemetry error span(s): {string.Join(", ", failingSpans.Select(static s => s.OperationName))}.")));
        foreach (var span in observedSpans.TakeLast(6))
        {
            traces.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"span otel.{span.OperationName} status={(span.IsError ? "ERROR" : "OK")} durationMs={span.DurationMs}"));
        }

        // Live brain ITraceReader causal journal telemetry audit.
        if (liveContext?.TraceReader is { } traceReader && !string.IsNullOrWhiteSpace(liveContext.TraceId))
        {
            try
            {
                var tracePage = await traceReader.ReadTrace(liveContext.TraceId, since: 0, limit: 500).ConfigureAwait(false);
                var errorTraceEntries = tracePage.Entries.Where(IsErrorTraceEntry).ToArray();
                errorLogs += errorTraceEntries.Length;
                var traceClean = errorTraceEntries.Length == 0;
                probes.Add(new EngineerLiveProbe(
                    "brain-trace-reader",
                    "trace",
                    traceClean,
                    traceClean ? 200 : 500,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"ITraceReader verified {tracePage.Entries.Length} causal signal(s) on trace '{liveContext.TraceId}' with {errorTraceEntries.Length} error signal(s).")));
                foreach (var entry in tracePage.Entries.TakeLast(6))
                {
                    traces.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"trace.journal seq={entry.Sequence} signal={entry.Signal} publisher={entry.Publisher}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, $"ITraceReader telemetry probe failed for trace '{liveContext.TraceId}'.");
                errorLogs++;
                probes.Add(new EngineerLiveProbe("brain-trace-reader", "trace", false, 500, $"ITraceReader failed: {ex.Message}"));
            }
        }

        var screensToInspect = ResolveScreensToInspect(liveContext, _services);
        var mcpPassed = !failedClosed;
        if (!string.IsNullOrWhiteSpace(mcpUrl) && hasHttpTransport)
        {
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "mcp-behavioral-probe", "mcp", mcpUrl, ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            mcpPassed = probe.Passed;
            traces.Add($"span mcp.probe endpoint={mcpUrl} passed={probe.Passed}");
        }
        else if (!failedClosed && screensToInspect.Count == 0 && !string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttpTransport)
        {
            var derivedMcpUrl = liveBaseUrl.TrimEnd('/') + "/mcp";
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "mcp-behavioral-probe", "mcp", derivedMcpUrl, ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            mcpPassed = probe.Passed;
            traces.Add($"span mcp.probe endpoint={derivedMcpUrl} passed={probe.Passed}");
        }
        else if (failedClosed)
        {
            mcpPassed = false;
        }

        var playwrightPassed = true;
        var auditedScreenCount = 0;
        var uiViolationsTotal = 0;

        // MCP / IScreen live Open() + View-tree audit (non-null root, non-empty AutomationName/Key invariants, zero broken AppShellNode layout, zero error/crash nodes).
        if (screensToInspect.Count > 0)
        {
            foreach (var (screenName, screen) in screensToInspect)
            {
                try
                {
                    var screenView = await screen.Open().ConfigureAwait(false)
                        ?? await screen.Read().ConfigureAwait(false);
                    if (screenView is null)
                    {
                        mcpPassed = false;
                        if (touchesUi) { playwrightPassed = false; }
                        errorLogs++;
                        uiViolationsTotal++;
                        probes.Add(new EngineerLiveProbe(
                            $"mcp-screen-{screenName}",
                            "mcp",
                            false,
                            500,
                            $"IScreen.Open()/Read() returned null for '{screenName}'."));
                        continue;
                    }

                    auditedScreenCount++;
                    var enforceUiInvariants = touchesUi || HasViewRootEnvelope(screenView.View);
                    var audit = AuditScreenViewJson(screenView.View, enforceUiInvariants);
                    var screenHealthy = audit.NodeCount > 0
                        && audit.ErrorNodeCount == 0
                        && audit.Violations.Count == 0;
                    if (!screenHealthy)
                    {
                        mcpPassed = false;
                        if (touchesUi) { playwrightPassed = false; }
                        var issueCount = Math.Max(1, audit.ErrorNodeCount + audit.Violations.Count);
                        errorLogs += issueCount;
                        uiViolationsTotal += issueCount;
                    }

                    var violationSuffix = audit.Violations.Count > 0
                        ? $" Violations: {string.Join("; ", audit.Violations.Take(8))}"
                        : string.Empty;
                    probes.Add(new EngineerLiveProbe(
                        $"mcp-screen-{screenName}",
                        "mcp",
                        screenHealthy,
                        screenHealthy ? 200 : 500,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"IScreen '{screenName}' rendered {audit.NodeCount} node(s), {audit.KeyedCount} keyed control(s), {audit.ErrorNodeCount} error node(s).{violationSuffix}")));
                    traces.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"span mcp.screen target={screenName} nodes={audit.NodeCount} errorNodes={audit.ErrorNodeCount} violations={audit.Violations.Count}"));
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException && IsUnhostedScreenException(ex))
                {
                    _logger.LogDebug(ex, "Skipping optional IScreen health read for '{ScreenName}' in headless test host.", screenName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, $"IScreen health probe failed for '{screenName}'.");
                    mcpPassed = false;
                    if (touchesUi) { playwrightPassed = false; }
                    errorLogs++;
                    uiViolationsTotal++;
                    probes.Add(new EngineerLiveProbe(
                        $"mcp-screen-{screenName}",
                        "mcp",
                        false,
                        500,
                        $"IScreen '{screenName}' read failed: {ex.Message}"));
                }
            }
        }

        if (touchesUi)
        {
            var uiTreeGateOk = uiViolationsTotal == 0;
            probes.Add(new EngineerLiveProbe(
                "ui-view-tree-gate",
                "playwright",
                uiTreeGateOk,
                uiTreeGateOk ? 200 : 500,
                uiTreeGateOk
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"UI View tree gate verified {auditedScreenCount} live screen(s) (non-null root, non-empty AutomationName/Key invariants, valid AppShellNode layout).")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"UI View tree gate detected {uiViolationsTotal} view/layout/accessibility violation(s).")));

            if (!string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttpTransport)
            {
                var uiHttpProbe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "ui-loopback-http-gate", "playwright", liveBaseUrl.TrimEnd('/') + "/", ct).ConfigureAwait(false);
                probes.Add(uiHttpProbe);
                if (uiHttpProbe.HttpStatus is >= 400 and < 500) { count4xx++; }
                if (uiHttpProbe.HttpStatus is >= 500) { count5xx++; }
                if (!uiHttpProbe.Passed)
                {
                    playwrightPassed = false;
                }

                traces.Add($"span ui.loopback status={uiHttpProbe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={uiHttpProbe.Passed}");
            }
        }

        // Optional live Playwright page / browser session DOM probe when registered in DI.
        if (_services?.GetService<IntoChat.OS.Modules.Microsoft.Playwright.IBrowserPageSession>() is { } pageSession)
        {
            var (probe, p4xx, p5xx, pErr) = await ProbeBrowserPageSessionAsync(pageSession, touchesUi, ct).ConfigureAwait(false);
            probes.Add(probe);
            count4xx += p4xx;
            count5xx += p5xx;
            errorLogs += pErr;
            if (!probe.Passed)
            {
                playwrightPassed = false;
            }

            traces.Add($"span playwright.page_session passed={probe.Passed} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
        }
        else if (_services?.GetService<IntoChat.OS.Modules.Microsoft.Playwright.IPlaywright>() is { } playwrightService)
        {
            var (probe, p4xx, p5xx, pErr) = await ProbePlaywrightServiceAsync(playwrightService, ct).ConfigureAwait(false);
            if (probe is not null)
            {
                probes.Add(probe);
                count4xx += p4xx;
                count5xx += p5xx;
                errorLogs += pErr;
                if (!probe.Passed)
                {
                    playwrightPassed = false;
                }

                traces.Add($"span playwright.live_dom passed={probe.Passed} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            }
        }

        // Verify UiNeuronPlaywrightExtensions contract readiness & Dart/UI surface integrity.
        var uiSemanticsMethod = typeof(IntoChat.OS.Modules.Microsoft.Playwright.UiNeuronPlaywrightExtensions).GetMethod(nameof(IntoChat.OS.Modules.Microsoft.Playwright.UiNeuronPlaywrightExtensions.KeyedNode));
        var uiReady = uiSemanticsMethod is not null;
        probes.Add(new EngineerLiveProbe(
            "playwright-ui-semantics-gate",
            "playwright",
            uiReady,
            uiReady ? 200 : 500,
            uiReady
                ? "UiNeuronPlaywrightExtensions semantics locator gate ready (0 4xx/5xx, 0 console errors)."
                : "UiNeuronPlaywrightExtensions missing."));
        if (!uiReady)
        {
            playwrightPassed = false;
            errorLogs++;
        }

        traces.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"span telemetry.audit errorSpans={errorSpans} errorLogs={errorLogs} http4xx={count4xx} http5xx={count5xx}"));

        var succeeded = hostHealthy
            && mcpPassed
            && playwrightPassed
            && count4xx == 0
            && count5xx == 0
            && errorSpans == 0
            && errorLogs == 0;

        var summary = failedClosed
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"LiveVerify=False · {PlaceboDisabledMessage} (Host={hostHealthy}, MCP={mcpPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}).")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"LiveVerify={succeeded} · Host={hostHealthy}, MCP={mcpPassed}, Playwright={playwrightPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}.");

        return new EngineerLiveReport(
            Succeeded: succeeded,
            HostHealthy: hostHealthy,
            McpProbesPassed: mcpPassed,
            PlaywrightPassed: playwrightPassed,
            Http4xxCount: count4xx,
            Http5xxCount: count5xx,
            ErrorSpanCount: errorSpans,
            ErrorLogCount: errorLogs,
            Probes: [.. probes],
            TraceSummaries: [.. traces],
            Summary: summary);
    }

    private static IReadOnlyList<(string ScreenName, IScreen Screen)> ResolveScreensToInspect(
        EngineerLiveContext? liveContext,
        IServiceProvider? services)
    {
        if (liveContext?.Screens is { Count: > 0 } explicitScreens)
        {
            return explicitScreens;
        }

        if (services is null)
        {
            return [];
        }

        var discovered = services.GetServices<IScreen>().ToArray();
        if (discovered.Length == 0)
        {
            return [];
        }

        var list = new List<(string, IScreen)>(discovered.Length);
        for (var i = 0; i < discovered.Length; i++)
        {
            list.Add(($"di-screen-{i + 1}", discovered[i]));
        }

        return list;
    }

    private async Task<(EngineerLiveProbe Probe, int Count4xx, int Count5xx, int ErrorLogs)> ProbeBrowserPageSessionAsync(
        IntoChat.OS.Modules.Microsoft.Playwright.IBrowserPageSession pageSession,
        bool required,
        CancellationToken ct)
    {
        if (!pageSession.Connected)
        {
            if (!required)
            {
                return (new EngineerLiveProbe("playwright-page-session", "playwright", true, 200, "Optional IBrowserPageSession idle (non-UI change)."), 0, 0, 0);
            }

            return (new EngineerLiveProbe("playwright-page-session", "playwright", false, 503, "IBrowserPageSession is disconnected during UI verification."), 0, 1, 1);
        }

        try
        {
            var obs = await pageSession.SnapshotAsync(ct).ConfigureAwait(false);
            return EvaluateBrowserObservation("playwright-page-session", obs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Live IBrowserPageSession snapshot probe failed.");
            return (new EngineerLiveProbe("playwright-page-session", "playwright", false, 500, $"Playwright snapshot failed: {ex.Message}"), 0, 1, 1);
        }
    }

    private async Task<(EngineerLiveProbe? Probe, int Count4xx, int Count5xx, int ErrorLogs)> ProbePlaywrightServiceAsync(
        IntoChat.OS.Modules.Microsoft.Playwright.IPlaywright playwrightService,
        CancellationToken ct)
    {
        try
        {
            var session = await playwrightService.Read().ConfigureAwait(false);
            if (!session.Connected)
            {
                return (null, 0, 0, 0);
            }

            var obs = await playwrightService.Snapshot(ct).ConfigureAwait(false);
            var (probe, c4, c5, err) = EvaluateBrowserObservation("playwright-live-dom-probe", obs);
            return (probe, c4, c5, err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Live IPlaywright DOM snapshot probe failed.");
            return (new EngineerLiveProbe("playwright-live-dom-probe", "playwright", false, 500, $"IPlaywright probe failed: {ex.Message}"), 0, 1, 1);
        }
    }

    private static (EngineerLiveProbe Probe, int Count4xx, int Count5xx, int ErrorLogs) EvaluateBrowserObservation(
        string probeName,
        IntoChat.OS.Modules.Microsoft.Playwright.BrowserObservation obs)
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

        return (
            new EngineerLiveProbe(
                probeName,
                "playwright",
                true,
                200,
                $"Playwright live page verified at '{obs.Url}' (title='{obs.Title}', {obs.Links.Length} link(s), 0 HTTP/DOM errors)."),
            0,
            0,
            0);
    }

}
