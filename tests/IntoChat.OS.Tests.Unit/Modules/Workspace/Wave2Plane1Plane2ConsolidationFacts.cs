using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Workspace;

public sealed class Wave2Plane1Plane2ConsolidationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SafeNotificationsScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                await os.Notifications.NotifyAsync("Digest", "Daily summary ready", NotificationPriority.Normal, ct);
                return new OrchestrationResult(
                    Status: "succeeded",
                    Summary: "Sent daily digest",
                    Artifacts: [new OrchestrationArtifact("digest.txt", "text", "ok")],
                    Metrics: new Dictionary<string, double> { ["sent"] = 1 });
            }
        }
        """;

    private const string ForbiddenProcessScript = """
        using System.Diagnostics;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                Process.Start("calc");
                return Task.FromResult(new OrchestrationResult("succeeded", "should never run"));
            }
        }
        """;

    [Fact]
    public void InoRoutesToICodeOrchestrationAndCodeBlockRunAndEngineerAppAreDeleted()
    {
        var def = InoAgentDefinition.For(new UserSettingsState { DisplayName = "Owner" });

        Assert.Contains(typeof(ICodeOrchestration).FullName!, def.Contracts);
        Assert.DoesNotContain(def.Contracts, static c => c.Contains("ICodeBlockRun", StringComparison.Ordinal));
        Assert.Contains("code_orchestration_run_async", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("code_orchestration_crystallize_async", def.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("codeblock_run_start", def.Instructions, StringComparison.Ordinal);

        var schemas = ToolSchemas.Of(typeof(ICodeOrchestration));
        Assert.Contains(schemas, static s => s.Name == "code_orchestration_run_async");
        Assert.Contains(schemas, static s => s.Name == "code_orchestration_crystallize_async");

        // CodeBlockRunNeuron and EngineerApp are completely removed from shipped assemblies.
        Assert.Null(typeof(AppsModule).Assembly.GetType("IntoChat.OS.Modules.Apps.CodeBlocks.CodeBlockRunNeuron"));
        Assert.Null(typeof(ICodeOrchestration).Assembly.GetType("IntoChat.OS.Modules.Apps.CodeBlocks.ICodeBlockRun"));
        Assert.Null(typeof(CreatorApp).Assembly.GetType("IntoChat.OS.Apps.Engineer.EngineerApp"));
        Assert.Null(typeof(CreatorApp).Assembly.GetType("IntoChat.OS.Apps.Engineer.EngineerWorkspaceState"));
        Assert.DoesNotContain(ComponentManifests.All, static m => m.Id.Value == "app.engineer");

        // CreatorApp is demoted from default Dock / primary user navigation to opt-in Developer / DevUI.
        var creatorManifest = Assert.Single(ComponentManifests.All, static m => m.Id.Value == "app.creator");
        Assert.NotNull(creatorManifest.Face);
        Assert.Null(creatorManifest.Face!.Dock);
        Assert.Equal("Developer", creatorManifest.Face.Category);
        Assert.DoesNotContain("creator", new DesktopAppState().PinnedApps ?? []);
    }

    [Fact]
    public async Task CodeOrchestrationSynapseFailsClosedWhenAstSanitizerIsUnavailableOrFlagsForbiddenSymbols()
    {
        var brain = new BrainId("owner");
        var sandbox = new RecordingSandboxBridge();

        // 1. Missing/unavailable AST sanitizer must fail closed immediately without touching sandbox.
        var unavailableServices = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton<ICodeOrchestrationAstSanitizer>(UnavailableCodeOrchestrationAstSanitizer.Instance)
            .AddSingleton<ISandboxRuntimeBridge>(sandbox)
            .BuildServiceProvider();

        await using (var os = TestOS.Local(o => o.Services = unavailableServices).Register<CodeOrchestrationSynapse>())
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            var neuron = os.Get<ICodeOrchestration>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "code-orchestration/no-sanitizer"));
            _ = await neuron.RunAsync("No sanitizer", "Run script", preauthoredScript: SafeNotificationsScript, ct: Ct);
            await os.Idle();
            var view = await neuron.ReadAsync(Ct);

            Assert.NotNull(view);
            Assert.Equal(FlowStatus.Failed, view!.Status);
            Assert.NotNull(view.Error);
            Assert.Contains("AST000", view.Error, StringComparison.Ordinal);
            Assert.Equal(0, sandbox.ExecuteCalls);
        }

        // 2. Real AST sanitizer rejects forbidden Process.Start symbol fail-closed.
        var strictServices = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton<ICodeOrchestrationAstSanitizer>(new CodeOrchestrationAstSanitizer())
            .AddSingleton<ISandboxRuntimeBridge>(sandbox)
            .BuildServiceProvider();

        await using (var os = TestOS.Local(o => o.Services = strictServices).Register<CodeOrchestrationSynapse>())
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            var neuron = os.Get<ICodeOrchestration>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "code-orchestration/forbidden-ast"));
            _ = await neuron.RunAsync("Forbidden process", "Launch process", preauthoredScript: ForbiddenProcessScript, ct: Ct);
            await os.Idle();
            var view = await neuron.ReadAsync(Ct);

            Assert.NotNull(view);
            Assert.Equal(FlowStatus.Failed, view!.Status);
            Assert.NotNull(view.Error);
            Assert.Equal(0, sandbox.ExecuteCalls);
        }
    }

    [Fact]
    public async Task CrystallizeAsyncDeploysViaUnitDeployerWithLeastPrivilegeScopedContractsInsideSandbox()
    {
        var brain = new BrainId("owner");
        var sandbox = new RecordingSandboxBridge();
        var unitDeployer = new RecordingSynapseHost();

        var services = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton<ICodeOrchestrationAstSanitizer>(new CodeOrchestrationAstSanitizer())
            .AddSingleton<ISandboxRuntimeBridge>(sandbox)
            .AddSingleton<ISynapseHost>(unitDeployer)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services).Register<CodeOrchestrationSynapse>();
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));

        var neuron = os.Get<ICodeOrchestration>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "code-orchestration/crystallize-ok"));
        _ = await neuron.RunAsync(
            title: "Daily Digest",
            goal: "Send daily digest notification",
            inputJson: "{}",
            allowedContracts: ["notifications"],
            preauthoredScript: SafeNotificationsScript,
            ct: Ct);
        await os.Idle();
        var run = await neuron.ReadAsync(Ct);

        Assert.NotNull(run);
        Assert.Equal(FlowStatus.Completed, run!.Status);
        Assert.Equal(1, sandbox.ExecuteCalls);

        var pin = await neuron.CrystallizeAsync("daily.digest", "Daily Digest App", Ct);

        Assert.True(pin.Succeeded, pin.Error);
        Assert.Equal("pinned-daily-digest", pin.AppKey);
        Assert.Equal("daily.digest", pin.Alias);
        Assert.Equal("Daily Digest App", pin.Title);
        Assert.Equal("apps/daily.digest/view", pin.LiveViewPath);
        Assert.Equal("owner/daily.digest/default", pin.EventTarget);
        Assert.Equal("IntoChat.OS.Sandbox", pin.ExecutionTarget);
        Assert.Equal(["notifications"], pin.ScopedContracts);
        Assert.DoesNotContain(pin.ScopedContracts, static c => c.Contains('*', StringComparison.Ordinal));

        // Verify IntoChat.OS.Sandbox DeployScopedPackageAsync received least-privilege ScopedContracts (never */*).
        Assert.Equal(1, sandbox.ScopedDeployCalls);
        Assert.Equal(["notifications"], sandbox.LastDeployedContracts);
        Assert.Equal("pinned-daily-digest", sandbox.LastDeployedAppKey);

        // Verify UnitDeployer (ISynapseHost) received the crystallized unit registration.
        Assert.Equal(1, unitDeployer.DeployCalls);
        Assert.Equal("daily.digest", unitDeployer.LastSourceKey);
    }

    [Fact]
    public async Task EngineerLiveVerifierWiresToICandidateHostFailsClosedAndScrubsHostCustodySecrets()
    {
        const string sampleFile = "Flows/EngineerFlow.cs";

        // 1. Unconfigured CandidateLiveVerifier fails closed (never placebo green).
        var unconfiguredBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(candidateHost: null);
        Assert.False(unconfiguredBridge.IsConfigured);

        var unconfiguredReport = await unconfiguredBridge.VerifyCandidateAsync(
            new CandidateLiveVerificationRequest(Path.GetTempPath(), "Check fail-closed", [sampleFile]),
            Ct);
        Assert.False(unconfiguredReport.Succeeded);
        Assert.False(unconfiguredReport.HostHealthy);
        Assert.Contains(unconfiguredReport.Probes, static p => p.Name == "candidate-host-required" && !p.Passed);

        // 2. Wired ICandidateHost via DI configures EngineerLiveVerifier, boots directly from CommitSha (no double-Prepare),
        //    and runs real /brains/{brainId}/mcp JSON-RPC checks (neurons_context + neurons_list).
        var fakeHost = new RecordingCandidateHost(healthy: true);
        var mcpHandler = new StubHttpHandler(HttpStatusCode.OK, "{\"status\":\"Healthy\"}");
        var okHttp = new HttpClient(mcpHandler);
        var hostBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(fakeHost, httpClient: okHttp, healthBudget: TimeSpan.FromSeconds(5));
        Assert.True(hostBridge.IsConfigured);

        await using var sp = new ServiceCollection()
            .AddSingleton<IEngineerCandidateHostVerifier>(hostBridge)
            .BuildServiceProvider();

        using var liveVerifier = new EngineerLiveVerifier(services: sp);
        Assert.True(liveVerifier.Configured);

        var passReport = await liveVerifier.VerifyLiveAsync(
            Path.GetTempPath(),
            "Verify candidate host path",
            [sampleFile],
            new EngineerLiveContext(CommitSha: "0123456789abcdef", BrainId: "owner"),
            Ct);

        Assert.True(passReport.Succeeded, passReport.Summary);
        Assert.True(passReport.HostHealthy);
        Assert.True(passReport.McpProbesPassed);
        Assert.Equal(["publish:0123456789ab", "run:0123456789ab", "healthy:0123456789ab", "remove:0123456789ab"], fakeHost.Calls);
        Assert.Contains(mcpHandler.PostPaths, static p => p.EndsWith("/brains/owner/mcp", StringComparison.Ordinal));

        // 2b. MCP JSON-RPC error fails closed even when /health returns 200.
        var errorMcpHost = new RecordingCandidateHost(healthy: true);
        var errorMcpHttp = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, "{\"status\":\"Healthy\"}", mcpJsonRpcError: true));
        var errorMcpBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(errorMcpHost, httpClient: errorMcpHttp, healthBudget: TimeSpan.FromSeconds(5));
        var errorMcpReport = await errorMcpBridge.VerifyCandidateAsync(
            new CandidateLiveVerificationRequest(Path.GetTempPath(), "MCP error fails closed", [sampleFile], CommitSha: "3334567890ab", BrainId: "owner"),
            Ct);
        Assert.False(errorMcpReport.Succeeded);
        Assert.False(errorMcpReport.McpProbesPassed);
        Assert.Contains("remove:3334567890ab", errorMcpHost.Calls);

        // 3. Failing candidate host fails closed and still cleans up the candidate.
        var failingHost = new RecordingCandidateHost(healthy: false);
        var failingBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(failingHost, httpClient: okHttp, healthBudget: TimeSpan.FromSeconds(5));
        using var failingVerifier = new EngineerLiveVerifier(candidateHostVerifier: failingBridge);

        var failReport = await failingVerifier.VerifyLiveAsync(
            Path.GetTempPath(),
            "Verify failing candidate fails closed",
            [sampleFile],
            new EngineerLiveContext(CommitSha: "abcdef012345"),
            Ct);

        Assert.False(failReport.Succeeded);
        Assert.False(failReport.HostHealthy);
        Assert.Contains("remove:abcdef012345", failingHost.Calls);

        // 3b. Null HttpClient / IHttpClientFactory fails closed, and invalid HealthPath is rejected fail-closed.
        var noHttpHost = new RecordingCandidateHost(healthy: true);
        var noHttpBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(noHttpHost, httpClient: null, healthBudget: TimeSpan.FromSeconds(5));
        var noHttpReport = await noHttpBridge.VerifyCandidateAsync(
            new CandidateLiveVerificationRequest(Path.GetTempPath(), "No http client", [sampleFile], CommitSha: "1234567890ab"),
            Ct);
        Assert.False(noHttpReport.Succeeded);
        Assert.False(noHttpReport.HostHealthy);
        Assert.False(noHttpReport.McpProbesPassed);
        Assert.False(noHttpReport.PlaywrightPassed);
        Assert.Contains("remove:1234567890ab", noHttpHost.Calls);

        var badPathHost = new RecordingCandidateHost(healthy: true);
        var badPathBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(badPathHost, httpClient: okHttp, healthBudget: TimeSpan.FromSeconds(5));
        var badPathReport = await badPathBridge.VerifyCandidateAsync(
            new CandidateLiveVerificationRequest(Path.GetTempPath(), "Bad health path", [sampleFile], CommitSha: "2234567890ab", HealthPath: "//evil.example/health"),
            Ct);
        Assert.False(badPathReport.Succeeded);
        Assert.False(badPathReport.HostHealthy);
        Assert.Empty(badPathHost.Calls);

        // 4. ProcessCandidateHost scrubs host custody keys and presence tokens before spawning candidate process.
        ProcessStartInfo? capturedStartInfo = null;
        var tempRoot = Path.Combine(Path.GetTempPath(), "intochat-proc-scrub-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            var procHost = new ProcessCandidateHost(
                processes: new NoOpProcessRunner(),
                workdir: _ => Task.FromResult(tempRoot),
                secrets: _ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>
                {
                    [IntoChatOSNames.MasterKeyEnvironmentVariable] = "LEAKED-HOST-MASTER-KEY",
                    ["INTOCHAT_PRESENCE_TOKEN"] = "LEAKED-PRESENCE-TOKEN",
                    [PresenceProof.TokenHashEnvironmentVariable] = "LEAKED-PRESENCE-HASH",
                    ["OPENAI_API_KEY"] = "sk-candidate-model-key",
                })),
                launcher: startInfo =>
                {
                    capturedStartInfo = startInfo;
                    return new CandidateProcessHandle(
                        pid: 4242,
                        hasExited: () => true,
                        exitCode: () => 0,
                        stdout: () => "",
                        stderr: () => "",
                        kill: _ => { },
                        waitForExit: _ => Task.CompletedTask);
                },
                http: okHttp,
                portAllocator: () => 51999);

            var running = await procHost.Run("abcdef1", Ct);
            Assert.NotNull(capturedStartInfo);
            Assert.NotEqual("LEAKED-HOST-MASTER-KEY", capturedStartInfo!.Environment[IntoChatOSNames.MasterKeyEnvironmentVariable]);
            Assert.False(capturedStartInfo.Environment.ContainsKey("INTOCHAT_PRESENCE_TOKEN"));
            Assert.False(capturedStartInfo.Environment.ContainsKey(PresenceProof.TokenHashEnvironmentVariable));
            Assert.Equal("sk-candidate-model-key", capturedStartInfo.Environment["OPENAI_API_KEY"]);
            await procHost.Remove("abcdef1", Ct);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CandidateLiveVerifierRunsPlaywrightAgainstLiveUiBeforeTeardownAndCleansUpOnFailure()
    {
        const string uiFile = "ui/lib/src/nodes/app_shell.dart";
        const string expectedUiUrl = "http://127.0.0.1:51999/";
        using var okHttp = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, "{\"status\":\"Healthy\"}"));

        // 1. Passing TouchesUi=true run: Playwright navigates to live running.Endpoints.Ui BEFORE candidate Remove.
        var passTimeline = new List<string>();
        var passHost = new RecordingCandidateHost(healthy: true, timeline: passTimeline);
        var passSession = new RecordingBrowserPageSession(passTimeline, throwOnNavigate: null);
        var passBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(passHost, httpClient: okHttp, healthBudget: TimeSpan.FromSeconds(5));

        await using (var sp = new ServiceCollection()
            .AddSingleton<IEngineerCandidateHostVerifier>(passBridge)
            .AddSingleton<IBrowserPageSession>(passSession)
            .BuildServiceProvider())
        {
            using var verifier = new EngineerLiveVerifier(services: sp);
            var report = await verifier.VerifyLiveAsync(
                Path.GetTempPath(),
                "Verify UI Playwright runs before candidate teardown",
                [uiFile],
                new EngineerLiveContext(CommitSha: "aa1122334455", BrainId: "owner"),
                Ct);

            Assert.True(report.Succeeded, report.Summary);
            Assert.True(report.PlaywrightPassed);
            Assert.Equal([expectedUiUrl], passSession.NavigatedUrls);
            Assert.Contains(report.Probes, static p => p.Name == "playwright-page-session" && p.Passed);
            Assert.Equal(
                ["publish:aa1122334455", "run:aa1122334455", "healthy:aa1122334455", $"playwright.navigate:{expectedUiUrl}", "remove:aa1122334455"],
                passTimeline);
        }

        // 2. Failing Playwright NavigateAsync: Playwright still ran before Remove, report fails, and Remove runs in finally.
        var failTimeline = new List<string>();
        var failHost = new RecordingCandidateHost(healthy: true, timeline: failTimeline);
        var failSession = new RecordingBrowserPageSession(failTimeline, throwOnNavigate: new InvalidOperationException("Browser crashed during navigation"));
        var failBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(failHost, httpClient: okHttp, healthBudget: TimeSpan.FromSeconds(5));

        await using (var sp = new ServiceCollection()
            .AddSingleton<IEngineerCandidateHostVerifier>(failBridge)
            .AddSingleton<IBrowserPageSession>(failSession)
            .BuildServiceProvider())
        {
            using var verifier = new EngineerLiveVerifier(services: sp);
            var report = await verifier.VerifyLiveAsync(
                Path.GetTempPath(),
                "Verify candidate teardown runs when Playwright throws",
                [uiFile],
                new EngineerLiveContext(CommitSha: "bb1122334455", BrainId: "owner"),
                Ct);

            Assert.False(report.Succeeded);
            Assert.False(report.PlaywrightPassed);
            Assert.Equal([expectedUiUrl], failSession.NavigatedUrls);
            Assert.Contains(report.Probes, static p => p.Name == "playwright-page-session" && !p.Passed);
            Assert.Equal(
                ["publish:bb1122334455", "run:bb1122334455", "healthy:bb1122334455", $"playwright.navigate:{expectedUiUrl}", "remove:bb1122334455"],
                failTimeline);
        }

        // 3. Direct CandidateLiveVerifier VerifyUiAsync exception still cleans up in finally.
        var hookTimeline = new List<string>();
        var hookHost = new RecordingCandidateHost(healthy: true, timeline: hookTimeline);
        var hookBridge = new IntoChat.Aspire.Verification.CandidateLiveVerifier(hookHost, httpClient: okHttp, healthBudget: TimeSpan.FromSeconds(5));
        var hookReport = await hookBridge.VerifyCandidateAsync(
            new CandidateLiveVerificationRequest(
                Path.GetTempPath(),
                "Verify direct VerifyUiAsync exception cleans up",
                [uiFile],
                CommitSha: "cc1122334455",
                TouchesUi: true,
                BrainId: "owner",
                VerifyUiAsync: (url, _) =>
                {
                    hookTimeline.Add($"verify-ui:{url}");
                    throw new InvalidOperationException("Playwright hook exploded");
                }),
            Ct);

        Assert.False(hookReport.Succeeded);
        Assert.False(hookReport.PlaywrightPassed);
        Assert.Contains(hookReport.Probes, static p => p.Name == "playwright-live-ui" && !p.Passed);
        Assert.Equal(
            ["publish:cc1122334455", "run:cc1122334455", "healthy:cc1122334455", $"verify-ui:{expectedUiUrl}", "remove:cc1122334455"],
            hookTimeline);
    }

    private sealed class RecordingSandboxBridge : ISandboxRuntimeBridge
    {
        public int ExecuteCalls { get; private set; }
        public int ScopedDeployCalls { get; private set; }
        public string[] LastDeployedContracts { get; private set; } = [];
        public string? LastDeployedAppKey { get; private set; }

        public Task<SandboxDeploymentResult> DeployPackageAsync(string brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));

        public Task<SandboxDeploymentResult> DeployScopedPackageAsync(BrainId brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, IReadOnlyList<string> allowedContracts, CancellationToken cancellationToken = default)
        {
            ScopedDeployCalls++;
            LastDeployedAppKey = package.Key;
            LastDeployedContracts = [.. allowedContracts];
            return Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));
        }

        public Task<SandboxDispatchResult> ExecuteScopedCommandAsync(BrainId brainId, AppPackage package, byte[] assemblyBytes, string command, string? payloadJson, IReadOnlyList<string> allowedContracts, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            return Task.FromResult(new SandboxDispatchResult(
                Handled: true,
                ResultJson: JsonSerializer.Serialize(new
                {
                    status = "succeeded",
                    summary = "Sent daily digest",
                    artifacts = new[] { new { name = "digest.txt", kind = "text", content = "ok" } },
                    metrics = new Dictionary<string, double> { ["sent"] = 1 },
                }),
                UpdatedStateJson: null,
                UpdatedView: null,
                Error: null,
                ExecutionTarget: "local-unit-host"));
        }

        public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default) => Task.FromResult<DigitalBrain.UI.View?>(null);
        public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSynapseHost : ISynapseHost
    {
        public int DeployCalls { get; private set; }
        public string? LastSourceKey { get; private set; }

        public Task<DeployOutcome> Deploy(BrainId brain, SynapseSource source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindingsPerAlias, CancellationToken ct)
        {
            DeployCalls++;
            LastSourceKey = source.SourceKey;
            return Task.FromResult(new DeployOutcome(DeployStatus.Deployed, "sha256:unit-hash", [], new Dictionary<string, InstallOutcome>(), null));
        }

        public Task<DeployOutcome> Retire(BrainId brain, string sourceKey, CancellationToken ct)
            => Task.FromResult(new DeployOutcome(DeployStatus.Retired, null, [], new Dictionary<string, InstallOutcome>(), null));

        public Task<IReadOnlyList<DeployedSource>> List(BrainId brain, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeployedSource>>([]);
    }

    private sealed class RecordingCandidateHost(bool healthy, List<string>? timeline = null) : ICandidateHost
    {
        public List<string> Calls { get; } = timeline ?? [];

        public Task<PreparedCandidate> Prepare(string baseSha, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
            => Task.FromResult(new PreparedCandidate("abcdef1", Path.GetTempPath()));

        public Task BuildGate(string sha, CancellationToken ct) => Task.CompletedTask;

        public Task<string> Publish(string sha, CancellationToken ct)
        {
            Calls.Add($"publish:{sha}");
            return Task.FromResult(Path.GetTempPath());
        }

        public Task<RunningCandidate> Run(string sha, CancellationToken ct)
        {
            Calls.Add($"run:{sha}");
            return Task.FromResult(new RunningCandidate(sha, new CandidateEndpoints("http://127.0.0.1:51999", "http://127.0.0.1:51999"), $"proc-{sha}"));
        }

        public Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
        {
            Calls.Add($"healthy:{sha}");
            if (!healthy)
            {
                throw new CandidateHostException("healthy", $"proc-{sha} exited with code 1");
            }

            return Task.CompletedTask;
        }

        public Task<string?> Commit(string sha, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task Uncommit(string sha, string? previous, CancellationToken ct) => Task.CompletedTask;

        public Task Remove(string sha, CancellationToken ct)
        {
            Calls.Add($"remove:{sha}");
            return Task.CompletedTask;
        }

        public Task<string[]> Orphans(string[] known, CancellationToken ct) => Task.FromResult<string[]>([]);
    }

    private sealed class StubHttpHandler(HttpStatusCode status, string body, bool mcpJsonRpcError = false) : HttpMessageHandler
    {
        public List<string> PostPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri is { } uri)
            {
                PostPaths.Add(uri.AbsolutePath);
                var mcpBody = mcpJsonRpcError
                    ? """{"jsonrpc":"2.0","id":1,"result":{"isError":true,"content":[{"type":"text","text":"tool failed"}]}}"""
                    : """{"jsonrpc":"2.0","id":1,"result":{"isError":false,"content":[{"type":"text","text":"{\"brainId\":\"owner\"}"}]}}""";
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(mcpBody, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingBrowserPageSession(List<string> timeline, Exception? throwOnNavigate) : IBrowserPageSession
    {
        public List<string> NavigatedUrls { get; } = [];
        public bool Connected => true;
        public string? Url => NavigatedUrls.LastOrDefault();
        public string? Title => "IntoChat Candidate";

        public Task<BrowserObservation> NavigateAsync(string url, CancellationToken ct)
        {
            NavigatedUrls.Add(url);
            timeline.Add($"playwright.navigate:{url}");
            if (throwOnNavigate is not null) { throw throwOnNavigate; }
            return Task.FromResult(new BrowserObservation(url, "IntoChat Candidate", "Candidate UI ready", []));
        }

        public Task<BrowserObservation> SnapshotAsync(CancellationToken ct)
        {
            timeline.Add("playwright.snapshot");
            return Task.FromResult(new BrowserObservation(Url ?? string.Empty, "IntoChat Candidate", "Candidate UI ready", []));
        }

        public Task<BrowserObservation> ClickAsync(string selector, CancellationToken ct) => SnapshotAsync(ct);
        public Task<BrowserObservation> FillAsync(string selector, string value, CancellationToken ct) => SnapshotAsync(ct);
        public Task<string> EvaluateAsync(string expression, CancellationToken ct) => Task.FromResult("{}");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, "", ""));

        public int Detach(string file, string[] args, string workingDirectory) => 0;
    }
}
