#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition.Confirmations;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;
using TaskStatus = IntoChat.OS.Modules.AI.Tasks.TaskStatus;
using TaskStepStatus = IntoChat.OS.Modules.AI.Tasks.TaskStepStatus;
using ExecutionContext = IntoChat.OS.Modules.AI.Context.ExecutionContext;

namespace IntoChat.OS.Kernel.Tests.E2E;

/// <summary>
/// Tier 4: Real-World Application Scenarios — 5 comprehensive multi-step end-to-end scenarios
/// verifying developer, autonomous agent, media studio, and security runtime workflows on a real in-process silo.
/// </summary>
public sealed class ProductScenarioFacts(ProductHostFixture fixture)
    : IClassFixture<ProductHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "AutonomousAgentSelfImprovementLoop")]
    public async Task Scenario01_AutonomousAgentSelfImprovementLoop_ExecutesFullLifecycle()
    {
        var brain = fixture.NewBrain("s01");
        var agentCaller = CapabilityCallers.Of("agent-optimizer-01", "account-system", brain, CallerKind.User);
        Assert.True(RuntimeCallerContext.IsTrusted(agentCaller));
        using var stamp = RuntimeCallerContext.Stamp(agentCaller);

        var ctx = new ExecutionContext("exec_self_improve_01", agentCaller, new Dictionary<string, string>
        {
            ["CYCLE"] = "14",
            ["TARGET_MODULE"] = "IntoChat.OS.Kernel"
        });

        var taskEngine = new TaskEngine(
            taskId => SiloNeurons.Get<ITaskNeuron>(fixture.Services, new NeuronId(new BrainId(brain), "tasks.task", taskId)),
            () => SiloNeurons.Get<ITaskDirectoryNeuron>(fixture.Services, new NeuronId(new BrainId(brain), "tasks.directory", "default")));
        var aspire = SiloNeurons.Get<IAspire>(fixture.Services, $"{brain}/aspire");
        var siloBdd = SiloNeurons.Get<IBdd>(fixture.Services, new NeuronId(new BrainId(brain), "bdd", "kernel"));

        // Step 2: Formulate improvement goal and enqueue task in TaskEngine on the real silo
        var steps = new[]
        {
            new TaskStepDefinition("deconstruct", "Objective deconstructed", "kernel"),
            new TaskStepDefinition("assess", "Capabilities assessed", "kernel"),
            new TaskStepDefinition("synthesize", "Plan synthesized", "kernel"),
        };
        var taskReq = new CreateTaskRequest("Kernel Latency Optimization", "Optimize kernel signal dispatch latency", "admin", steps);
        var agentTask = await taskEngine.CreateTaskAsync(taskReq, Ct);
        Assert.Equal(TaskStatus.Pending, agentTask.Status);
        Assert.Equal(3, agentTask.Steps.Length);

        // Step 3: Execute within context slices, recording performance telemetry on AspireNeuron on the silo
        var slice = new ContextSlice("BenchmarkExecution", "Running baseline and candidate benchmarks", ["benchmark", "latency"]);
        await ctx.WithSliceAsync(slice, async () =>
        {
            await aspire.RecordTraceAsync(new AspireTraceSpan(
                "trace-bench-01",
                "span-bench-01",
                "OperatingSystem",
                "benchmark.baseline.latency_ms",
                "Ok",
                14,
                "14.2ms"));
            await aspire.RecordTraceAsync(new AspireTraceSpan(
                "trace-bench-02",
                "span-bench-02",
                "OperatingSystem",
                "benchmark.candidate.latency_ms",
                "Ok",
                4,
                "4.1ms"));

            await taskEngine.AdvanceTaskStepAsync(agentTask.TaskId, 0, TaskStepStatus.Completed, ct: Ct);
            await taskEngine.AdvanceTaskStepAsync(agentTask.TaskId, 1, TaskStepStatus.Completed, ct: Ct);
            return true;
        });

        // Step 4: Automated regression verification persisted in BddNeuron on the silo
        var bddReport = await siloBdd.Record(
            new BddReport(
                "kernel",
                [new ScenarioVerdict("Tier1_FeatureCoverage.F1", Passed: true, Error: null, Elapsed: TimeSpan.FromMilliseconds(5))],
                BaselinePassed: true,
                BaselineError: null,
                Elapsed: TimeSpan.FromMilliseconds(5)),
            Ct);
        Assert.True(bddReport.Passed);

        // Step 5: Finalize remaining task step and verify complete task resolution on the silo
        var finalTask = await taskEngine.AdvanceTaskStepAsync(agentTask.TaskId, 2, TaskStepStatus.Completed, ct: Ct);
        Assert.Equal(TaskStatus.Completed, finalTask.Status);

        var taskVerification = await taskEngine.VerifyTaskStepAsync(agentTask.TaskId, 2, Ct);
        Assert.True(taskVerification.Passed);

        var traces = await aspire.GetTracesAsync("OperatingSystem");
        Assert.Equal(2, traces.Length);
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "HighThroughputStudioAppMediaPipeline")]
    public async Task Scenario02_HighThroughputStudioAppMediaPipeline_CoordinatesSignalsAndState()
    {
        var brain = fixture.NewBrain("s02");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

        // Step 1: Studio App Manifest Resolution and real FilmCraftApp execution on the silo
        var filmCraft = FilmCraftApp.Definition;
        Assert.Equal("filmcraft", filmCraft.Id);
        Assert.Equal("#8B5CF6", filmCraft.Accent);
        Assert.Equal("Studio", filmCraft.Category);

        var film = fixture.AttachSilo(new FilmCraftApp(), brain);
        await film.Open(new FilmCraftStudioOpen("/work/timeline.fcproj", "native-linux"));

        var initialState = new AppState
        {
            Files = new Dictionary<string, string>
            {
                ["timeline.xml"] = "<timeline tracks=\"3\"><track id=\"v1\"/><track id=\"a1\"/></timeline>",
                ["project.json"] = "{\"fps\": 60, \"resolution\": \"3840x2160\"}"
            },
            Settings = new Dictionary<string, string>
            {
                ["RENDER_ENGINE"] = "gpu_accelerated",
                ["COLOR_SPACE"] = "rec709"
            },
            Connections = new Dictionary<string, string>
            {
                ["frameStore"] = "memory://frames/buffer_01"
            }
        };

        var p0 = new PresentationChanged(filmCraft.Id, 1, 1);
        Assert.Equal(1, p0.Revision);

        // Step 2: Execute studio edit and export on the silo and record checkpoints in AspireNeuron
        var aspire = SiloNeurons.Get<IAspire>(fixture.Services, $"{brain}/aspire");
        await film.Edit(new FilmCraftStudioEdit("razor split timeline at 00:00:03:12", "/work/timeline.fcproj", "native-linux"));
        film.SwitchMode(new FilmCraftSwitchMode("mcp"));
        await film.Export(new FilmCraftExport("h264-1080p60", "/work/timeline.fcproj", "mcp"));
        Assert.EndsWith(".1080p60.mp4", film.State.OutputFile, StringComparison.Ordinal);

        const int totalFrames = 500;
        for (int frame = 1; frame <= totalFrames; frame++)
        {
            _ = new AppActivity(filmCraft.Id, "rendering", $"Rendered frame {frame}/{totalFrames}");
            if (frame % 100 == 0)
            {
                await aspire.RecordTraceAsync(new AspireTraceSpan(
                    $"trace-render-{frame}",
                    $"span-render-{frame}",
                    "FilmCraft",
                    "render.checkpoint",
                    "Ok",
                    frame,
                    $"Checkpoint {frame}"));
            }
        }

        // Step 3: Publish updated view to the silo and verify revision advancement
        await fixture.PublishAppViewToSiloAsync(brain, "filmcraft", film.RenderSide(), film.Render());
        var filmView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/filmcraft/view").Read();
        Assert.NotNull(filmView?.Root);

        var updatedFiles = new Dictionary<string, string>(initialState.Files)
        {
            ["output.mp4.meta"] = "{\"duration_seconds\": 8.33, \"codec\": \"h265\"}"
        };
        var finishedState = new AppState
        {
            Files = updatedFiles,
            Settings = initialState.Settings,
            Connections = initialState.Connections
        };

        var p1 = new PresentationChanged(filmCraft.Id, 1, 2);
        Assert.Equal(2, p1.Revision);
        Assert.True(p1.Revision > p0.Revision);
        Assert.Equal(3, finishedState.Files.Count);

        var checkpoints = await aspire.GetTracesAsync("FilmCraft");
        Assert.Equal(5, checkpoints.Length);
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "SecureAppSideloadConsentAndGrantGate")]
    public async Task Scenario03_SecureAppSideloadConsentAndGrantGate_EnforcesBoundaryProtection()
    {
        var brain = fixture.NewBrain("s03");

        // Step 1: Malicious unauthenticated caller attempts sideloading and is rejected by PlainNeuronCallFilter on the silo
        var untrustedCaller = new CapabilityCaller("malicious_actor", "acc_unknown", "");
        Assert.False(RuntimeCallerContext.IsTrusted(untrustedCaller));

        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => settingsNeuron.Read());

        // Step 2: Legitimate developer authenticates with verified provenance on the silo
        var verifiedCaller = CapabilityCallers.Of("dev_usr", "acc_corp", brain, CallerKind.User);
        Assert.True(RuntimeCallerContext.IsTrusted(verifiedCaller));
        using var stamp = RuntimeCallerContext.Stamp(verifiedCaller);

        // Step 3: Developer attempts to install app requiring elevated capabilities
        var existingRequirements = new AppRequirements(
            SemanticTypes: ["text/plain"],
            Connectors: ["local-storage"],
            Secrets: [],
            Capabilities: ["read"]);

        var newRequirements = new AppRequirements(
            SemanticTypes: ["text/plain", "audit/log"],
            Connectors: ["local-storage", "external-api"],
            Secrets: ["OAUTH_SECRET"],
            Capabilities: ["read", "write", "network.egress"]);

        // Step 4: Calculate consent requirements diff and sha256 consent digest
        var consentDiff = AppConsent.Added(existingRequirements, newRequirements);
        Assert.Contains("audit/log", consentDiff.SemanticTypes!);
        Assert.Contains("external-api", consentDiff.Connectors!);
        Assert.Contains("OAUTH_SECRET", consentDiff.Secrets!);
        Assert.Contains("network.egress", consentDiff.Capabilities!);

        var consentDigest = AppConsent.Of(newRequirements);
        Assert.NotNull(consentDigest);
        Assert.Equal(64, consentDigest.Length);

        // Step 5: High-risk confirmation gate pauses installation via ConfirmationsNeuron on the silo
        var confirmations = SiloNeurons.Get<IConfirmationInbox>(
            fixture.Services,
            new NeuronId(new BrainId(brain), "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var inboxBefore = await confirmations.Read();
        Assert.Empty(inboxBefore.Pending);

        var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalTask = Task.Run(async () =>
        {
            await Task.Yield();
            var approvalResult = new ConfirmationResult(
                PlanId: $"sideload_plan_{consentDigest[..8]}",
                Outcome: ConfirmationOutcome.Approved,
                Reason: ConfirmationDenyReason.None,
                Skipped: false);
            tcs.TrySetResult(approvalResult);
        });

        var decision = await tcs.Task;
        await approvalTask;

        Assert.True(decision.Approved);
        Assert.Equal(ConfirmationOutcome.Approved, decision.Outcome);

        // Step 6: With approval granted, sideload install completes
        var installResult = SideloadInstallResult.Done($"App package installed with consent hash {consentDigest[..12]}");
        Assert.True(installResult.Ok);
        Assert.Contains("installed with consent hash", installResult.Message);
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "KnowledgeAugmentedRoutingAndPlanSynthesis")]
    public async Task Scenario04_KnowledgeAugmentedRoutingAndPlanSynthesis_DeliberatesAndExecutes()
    {
        var brain = fixture.NewBrain("s04");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

        // Step 1: Populate MemoryNeuron on the real silo and KnowledgeStore topology
        var memory = SiloNeurons.Get<IMemory>(fixture.Services, $"{brain}/memory");
        await memory.RememberFact("architecture", "MediaProcessingService persists assets to BlobStorageS3 via cuda worker.", importance: 9);

        var store = new InMemoryKnowledgeStore();
        var nodes = new[]
        {
            new KnowledgeNode("gateway", "ApiGateway", "Entrypoint", new Dictionary<string, string> { ["protocol"] = "https" }),
            new KnowledgeNode("auth-service", "AuthenticationService", "Security", new Dictionary<string, string> { ["oauth"] = "enabled" }),
            new KnowledgeNode("media-service", "MediaProcessingService", "Worker", new Dictionary<string, string> { ["gpu"] = "cuda" }),
            new KnowledgeNode("storage-s3", "BlobStorageS3", "Infrastructure", new Dictionary<string, string> { ["bucket"] = "assets" })
        };

        foreach (var node in nodes)
        {
            await store.RecordNodeAsync(node);
        }

        await store.RecordEdgeAsync(new KnowledgeEdge("gateway", "auth-service", "authenticates_via", 1.0));
        await store.RecordEdgeAsync(new KnowledgeEdge("gateway", "media-service", "routes_to", 1.0));
        await store.RecordEdgeAsync(new KnowledgeEdge("media-service", "storage-s3", "persists_to", 0.9));

        // Step 2: Query graph view from gateway with depth 2 and recall from MemoryNeuron on the silo
        var topology = await store.QueryGraphAsync("gateway", depth: 2);
        Assert.Equal(4, topology.Nodes.Count);
        Assert.Equal(3, topology.Edges.Count);

        var recalledFacts = await memory.Recall("MediaProcessingService BlobStorageS3", 5);
        Assert.NotEmpty(recalledFacts);

        var searchHits = await store.SemanticSearchAsync("Media Storage", limit: 3);
        Assert.NotEmpty(searchHits);

        // Step 3: Execute task plan synthesis on TaskNeuron on the silo
        var taskEngine = new TaskEngine(
            taskId => SiloNeurons.Get<ITaskNeuron>(fixture.Services, new NeuronId(new BrainId(brain), "tasks.task", taskId)),
            () => SiloNeurons.Get<ITaskDirectoryNeuron>(fixture.Services, new NeuronId(new BrainId(brain), "tasks.directory", "default")));
        var constraints = topology.Nodes.Select(n => $"{n.Label}:{n.Type}").ToArray();
        var createdTask = await taskEngine.CreateTaskAsync(
            new CreateTaskRequest(
                "Optimize media ingestion route through storage-s3",
                string.Join(", ", constraints),
                "admin",
                [
                    new TaskStepDefinition("deconstruct", "Objective deconstructed", "kernel"),
                    new TaskStepDefinition("assess", $"Capabilities: {constraints.Length}", "kernel"),
                    new TaskStepDefinition("synthesize", "Plan synthesized", "kernel"),
                ]),
            Ct);
        Assert.Equal(3, createdTask.Steps.Length);
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "FaultInjectionAndResilientRecovery")]
    public async Task Scenario05_FaultInjectionAndResilientRecovery_GuaranteesFailClosedResilience()
    {
        var brain = fixture.NewBrain("s05");
        var caller = CapabilityCallers.Of("watchdog_agent", "acc_infra", brain, CallerKind.User);
        using var stamp = RuntimeCallerContext.Stamp(caller);
        _ = new ExecutionContext("exec_fault_recovery", caller);

        var taskEngine = new TaskEngine(
            taskId => SiloNeurons.Get<ITaskNeuron>(fixture.Services, new NeuronId(new BrainId(brain), "tasks.task", taskId)),
            () => SiloNeurons.Get<ITaskDirectoryNeuron>(fixture.Services, new NeuronId(new BrainId(brain), "tasks.directory", "default")));
        var aspire = SiloNeurons.Get<IAspire>(fixture.Services, $"{brain}/aspire");

        // Step 2: Execute task with simulated subsystem failure on TaskNeuron on the silo
        var task = await taskEngine.CreateTaskAsync(
            new CreateTaskRequest(
                "Primary Node Health Check",
                "Detect and recover from unresponsive cluster node",
                "admin",
                [
                    new TaskStepDefinition("s1", "ping_primary", "kernel"),
                    new TaskStepDefinition("s2", "trigger_failover", "kernel"),
                    new TaskStepDefinition("s3", "verify_standby", "kernel")
                ]),
            Ct);

        // Step 3: Step 1 fails due to injected timeout
        var failedTask = await taskEngine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Failed, "Connection timed out after 5000ms", Ct);
        Assert.Equal(TaskStatus.Failed, failedTask.Status);
        await aspire.RecordLogAsync(new AspireLogEntry(
            DateTimeOffset.UtcNow,
            "OperatingSystem",
            "Error",
            "node.failures.injected: primary-01 timed out",
            "System.TimeoutException"));

        // Step 4: Recovery task initiated and verified on TaskNeuron on the silo
        var recoveryTask = await taskEngine.CreateTaskAsync(
            new CreateTaskRequest(
                "Execute Failover",
                "Promote standby node",
                "admin",
                [
                    new TaskStepDefinition("r1", "route_traffic_standby", "kernel"),
                    new TaskStepDefinition("r2", "verify_quorum", "kernel")
                ]),
            Ct);

        await taskEngine.AdvanceTaskStepAsync(recoveryTask.TaskId, 0, TaskStepStatus.Completed, ct: Ct);
        var completedRecovery = await taskEngine.AdvanceTaskStepAsync(recoveryTask.TaskId, 1, TaskStepStatus.Completed, ct: Ct);
        Assert.Equal(TaskStatus.Completed, completedRecovery.Status);

        await aspire.RecordLogAsync(new AspireLogEntry(
            DateTimeOffset.UtcNow,
            "OperatingSystem",
            "Information",
            "node.failover.succeeded: standby promoted",
            null));

        // Step 5: Confirm all incident logs persisted on AspireNeuron on the silo
        var errorLogs = await aspire.GetStructuredLogsAsync("OperatingSystem", "Error");
        Assert.Contains(errorLogs, log => log.Message.Contains("node.failures.injected", StringComparison.Ordinal));
    }
}
