using System.Diagnostics;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class EngineerLiveVerifierFacts : IDisposable
{
    private static readonly string Apps = SolutionPaths.Directory("IntoChat.OS.Apps");
    private static readonly string Catalog = SolutionPaths.Directory("IntoChat.OS.Modules.Catalog");
    private static readonly string Aspire = SolutionPaths.Directory("IntoChat.Aspire");
    private static readonly string AppHost = SolutionPaths.Directory("IntoChat.OS.AppHost");
    private static readonly string Web = SolutionPaths.Directory("IntoChat.OS.Modules.Web");
    private static readonly string Ui = SolutionPaths.Directory("IntoChat.OS.UI");

    private readonly string _worktree;

    public EngineerLiveVerifierFacts()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "intochat-live-verifier-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_worktree);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_worktree))
            {
                Directory.Delete(_worktree, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void AChangeToAComponentScreenPlansAReadOfItsApp()
    {
        string[] changed = [$"{Aspire}/Screens/AspireApp.cs"];

        Assert.True(EngineerLiveVerifier.TouchesUiSurface(changed));
        Assert.Contains(EngineerLiveVerifier.DeriveVerificationPlan(changed).McpCalls, call => call.Target == "app.aspire" && call.Method == "Read");
    }

    [Fact]
    public void AChangeToComponentCodeOutsideScreensIsNotAUiChange()
    {
        string[] changed = [$"{Aspire}/Neurons/Telemetry/AspireNeuron.cs"];

        Assert.False(EngineerLiveVerifier.TouchesUiSurface(changed));
        Assert.DoesNotContain(EngineerLiveVerifier.DeriveVerificationPlan(changed).McpCalls, call => call.Target.StartsWith("app.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Settings/SettingsApp.cs", "app.settings", true)]
    [InlineData("IntoChat.OS.Apps/Orleans/OrleansApp.cs", "app.orleans", false)]
    public void AChangeToAShellAppStillPlansAReadOfItsApp(string path, string target, bool underApps)
    {
        var full = underApps ? $"{Apps}/{path}" : path;
        Assert.True(EngineerLiveVerifier.TouchesUiSurface([full]));
        Assert.Contains(EngineerLiveVerifier.DeriveVerificationPlan([full]).McpCalls, call => call.Target == target && call.Method == "Read");
    }

    [Theory]
    [InlineData("Neurons/Telemetry/AspireNeuron.cs")]
    [InlineData("Contracts/IAspire.cs")]
    [InlineData("AspireModule.cs")]
    public void AChangeToComponentCodePlansAListOfItsModule(string relativePath)
    {
        var calls = EngineerLiveVerifier.DeriveVerificationPlan([$"{Aspire}/{relativePath}"]).McpCalls;

        Assert.Equal([new EngineerVerificationCall("module.aspire", "neurons_list", "ok")], calls);
    }

    [Fact]
    public void AChangeToComponentHostingPlansWhatAnAppHostChangePlans()
    {
        var hosting = EngineerLiveVerifier.DeriveVerificationPlan([$"{Aspire}/Hosting/AspireModuleHosting.cs"]).McpCalls;
        var appHost = EngineerLiveVerifier.DeriveVerificationPlan([$"{AppHost}/Program.cs"]).McpCalls;

        Assert.Equal(appHost, hosting);
    }

    [Fact]
    public void AChangeToAModuleStillPlansAListOfItsModule()
        => Assert.Equal(
            [new EngineerVerificationCall("module.web", "neurons_list", "ok")],
            EngineerLiveVerifier.DeriveVerificationPlan([$"{Web}/WebModule.cs"]).McpCalls);

    [Fact]
    public void AChangeToTheUiKitIsAUiChange()
        => Assert.True(EngineerLiveVerifier.TouchesUiSurface([$"{Ui}/Kit.cs"]));

    [Fact]
    public async Task VerifyLiveAsync_WithNoConfiguredEndpointOrCandidate_RefusesPlaceboPass()
    {
        using var verifier = new EngineerLiveVerifier();

        var report = await verifier.VerifyLiveAsync(
            _worktree,
            "Ensure placebo verification is disabled",
            [Apps + "/Calculator/CalculatorApp.cs"],
            Ct);

        Assert.False(report.Succeeded);
        Assert.False(report.HostHealthy);
        Assert.False(report.McpProbesPassed);
        Assert.Contains(
            "live verification requires a candidate host or configured live endpoint; placebo verification is disabled",
            report.Summary,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(report.Probes, static p => p.Name == "in-process-worktree-host");
        Assert.DoesNotContain(report.Probes, static p => p.Name == "mcp-contract-probe");
        Assert.Contains(report.Probes, static p => p.Name == "candidate-host-required" && !p.Passed);
    }

    [Fact]
    public async Task VerifyLiveAsync_WhenCandidateReportsStartupExceptionOrErrorSpan_FailsWithErrorDetail()
    {
        // Case 1: Candidate throws a startup exception.
        using (var crashingVerifier = new EngineerLiveVerifier(
            candidateVerifier: (_, _) => throw new InvalidOperationException("Candidate kernel crashed during boot: missing binding")))
        {
            var crashReport = await crashingVerifier.VerifyLiveAsync(
                _worktree,
                "Verify startup exception fails live verification",
                [Apps + "/Engineer/EngineerApp.cs"],
                Ct);

            Assert.False(crashReport.Succeeded);
            Assert.False(crashReport.HostHealthy);
            Assert.True(crashReport.ErrorSpanCount >= 1);
            Assert.Contains("Candidate kernel crashed during boot", crashReport.Summary, StringComparison.Ordinal);
            Assert.Contains(crashReport.Probes, static p => p.Name == "candidate-host-startup" && !p.Passed);
        }

        // Case 2: Candidate returns a report containing an error span (even if Succeeded was optimistically set).
        using (var errorSpanVerifier = new EngineerLiveVerifier(
            candidateVerifier: (ctx, _) =>
            {
                Assert.NotNull(ctx.VerificationPlan);
                Assert.Equal("/health", ctx.VerificationPlan.HealthPath);
                Assert.True(ctx.VerificationPlan.RequireZeroErrorSpans);
                return Task.FromResult(new EngineerLiveReport(
                    Succeeded: false,
                    HostHealthy: true,
                    McpProbesPassed: true,
                    PlaywrightPassed: true,
                    Http4xxCount: 0,
                    Http5xxCount: 0,
                    ErrorSpanCount: 1,
                    ErrorLogCount: 0,
                    Probes: [new EngineerLiveProbe("candidate-otel-spans", "otel", false, 500, "Error span 'calculator.divide' (DivideByZeroException)")],
                    TraceSummaries: ["span calculator.divide status=ERROR"],
                    Summary: "Candidate reported 1 error span: calculator.divide (DivideByZeroException)"));
            }))
        {
            var spanReport = await errorSpanVerifier.VerifyLiveAsync(
                _worktree,
                "Verify error span fails live verification",
                [Apps + "/Calculator/CalculatorApp.cs"],
                Ct);

            Assert.False(spanReport.Succeeded);
            Assert.True(spanReport.ErrorSpanCount >= 1);
            Assert.Contains("DivideByZeroException", spanReport.Summary, StringComparison.Ordinal);
        }

        // Case 3: Candidate passes health/MCP, but a local OTel error span is captured on the same trace.
        using var localSpanVerifier = new EngineerLiveVerifier(
            candidateVerifier: (_, _) => Task.FromResult(new EngineerLiveReport(
                Succeeded: true,
                HostHealthy: true,
                McpProbesPassed: true,
                PlaywrightPassed: true,
                Http4xxCount: 0,
                Http5xxCount: 0,
                ErrorSpanCount: 0,
                ErrorLogCount: 0,
                Probes: [new EngineerLiveProbe("candidate-health", "host", true, 200, "GET /health -> 200")],
                TraceSummaries: ["span candidate.health status=OK"],
                Summary: "Candidate healthy")));

        localSpanVerifier.ClearCapturedSpans();
        using (var errActivity = EngineerTelemetry.Source.StartActivity("engineer.candidate-probe-error"))
        {
            errActivity?.SetTag("brain.trace_id", "trace-err-span");
            errActivity?.SetStatus(ActivityStatusCode.Error, "Injected error span");
        }

        var report = await localSpanVerifier.VerifyLiveAsync(
            _worktree,
            "Verify local OTel error span fails verification",
            [Catalog + "/Flows/EngineerFlow.cs"],
            new EngineerLiveContext(TraceId: "trace-err-span"),
            Ct);

        Assert.False(report.Succeeded);
        Assert.True(report.ErrorSpanCount >= 1);
        Assert.Contains(report.Probes, static p => p.Name == "otel-activity-spans" && !p.Passed);
    }

    [Fact]
    public async Task VerifyLiveAsync_WhenCandidatePassesHealthMcpAndZeroErrorSpans_Succeeds()
    {
        EngineerVerificationPlan? observedPlan = null;
        using var verifier = new EngineerLiveVerifier(
            candidateVerifier: (ctx, _) =>
            {
                observedPlan = ctx.VerificationPlan;
                return Task.FromResult(new EngineerLiveReport(
                    Succeeded: true,
                    HostHealthy: true,
                    McpProbesPassed: true,
                    PlaywrightPassed: true,
                    Http4xxCount: 0,
                    Http5xxCount: 0,
                    ErrorSpanCount: 0,
                    ErrorLogCount: 0,
                    Probes:
                    [
                        new EngineerLiveProbe("candidate-health", "host", true, 200, "GET /health -> HTTP 200"),
                        new EngineerLiveProbe("candidate-mcp", "mcp", true, 200, "MCP app.calculator Read -> ok"),
                        new EngineerLiveProbe("candidate-otel-spans", "otel", true, 200, "0 error spans"),
                    ],
                    TraceSummaries: ["span candidate.health status=OK", "span candidate.mcp status=OK"],
                    Summary: "Candidate verified: /health 200, MCP ok, 0 error spans."));
            });

        verifier.ClearCapturedSpans();
        var report = await verifier.VerifyLiveAsync(
            _worktree,
            "Verify healthy candidate succeeds",
            [Apps + "/Calculator/CalculatorApp.cs"],
            new EngineerLiveContext(TraceId: "trace-ok-cand"),
            Ct);

        Assert.NotNull(observedPlan);
        Assert.Equal("/health", observedPlan.HealthPath);
        Assert.True(observedPlan.RequireZeroErrorSpans);
        Assert.Contains(observedPlan.McpCalls, static c => c.Target == "app.calculator");

        Assert.True(report.Succeeded, report.Summary);
        Assert.True(report.HostHealthy);
        Assert.True(report.McpProbesPassed);
        Assert.Equal(0, report.ErrorSpanCount);
        Assert.Equal(0, report.Http4xxCount);
        Assert.Equal(0, report.Http5xxCount);
    }
}
