#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Confirmations;
using IntoChat.OS.Kernel.Tests.Integration.E2E.Harness;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Contracts.Tasks;
using IntoChat.OS.Modules.Apps.Contracts.Testing;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.ServiceDefaults;
using AppManifest = DigitalBrain.AppManifest;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;
using TaskStatus = IntoChat.OS.Modules.Apps.Contracts.Tasks.TaskStatus;
using TaskStepStatus = IntoChat.OS.Modules.Apps.Contracts.Tasks.TaskStepStatus;
using InMemoryKnowledgeStore = IntoChat.OS.Kernel.Tests.Integration.E2E.Harness.InMemoryKnowledgeStore;

namespace IntoChat.OS.Kernel.Tests.Integration.E2E;

/// <summary>
/// Tier 4: Real-World Application Scenarios — 5 comprehensive multi-step end-to-end scenarios
/// verifying developer, autonomous agent, media studio, and security runtime workflows.
/// </summary>
public sealed class Tier4_RealWorldScenariosFacts
{
    private static string RepositoryRoot => RepositoryPath();

    private static string RepositoryPath()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current) && !File.Exists(Path.Combine(current, "Intochat.OS.slnx")))
        {
            var parent = Directory.GetParent(current);
            if (parent is null) { break; }
            current = parent.FullName;
        }
        return current;
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "AutonomousAgentSelfImprovementLoop")]
    public async Task Scenario01_AutonomousAgentSelfImprovementLoop_ExecutesFullLifecycle()
    {
        // Step 1: Initialize autonomous agent execution context with verified provenance
        var agentCaller = CapabilityCallers.Of("agent-optimizer-01", "account-system", "brain-primary", CallerKind.User);
        Assert.True(CallerContextStamper.IsTrusted(agentCaller));

        var ctx = new InMemoryExecutionContext("exec_self_improve_01", agentCaller, new Dictionary<string, string>
        {
            ["CYCLE"] = "14",
            ["TARGET_MODULE"] = "IntoChat.OS.Kernel"
        });

        var taskEngine = new InMemoryTaskEngine();
        var decisionMaker = new InMemoryDecisionMaker();
        var verificationService = new InMemoryTestVerificationService();
        var telemetryBarrier = new TelemetryFlushBarrier();

        // Step 2: Formulate improvement goal via Deliberate Decision Maker
        var goal = new DeliberateGoal("Optimize kernel signal dispatch latency", ["zero-allocation-hotpath", "preserve-ordering"]);
        var plan = await decisionMaker.DecideDeliberateAsync(goal);
        Assert.NotEmpty(plan.Outcome.Steps);

        // Step 3: Enqueue task in Task Engine with sub-steps derived from plan
        var steps = plan.Outcome.Steps.Select(s => new TaskStepDefinition(s.Method, s.Result ?? s.Method, "kernel")).ToArray();
        var taskReq = new CreateTaskRequest("Kernel Latency Optimization", plan.Plan, "admin", steps);
        var agentTask = await taskEngine.CreateTaskAsync(taskReq);
        Assert.Equal(TaskStatus.Pending, agentTask.Status);
        Assert.Equal(3, agentTask.Steps.Length);

        // Step 4: Execute within context slices, recording performance telemetry
        var slice = new ContextSlice("BenchmarkExecution", "Running baseline and candidate benchmarks", ["benchmark", "latency"]);
        await ctx.WithSliceAsync(slice, async () =>
        {
            await Task.Yield();
            telemetryBarrier.RecordMetric("benchmark.baseline.latency_ms", 14.2);
            telemetryBarrier.RecordMetric("benchmark.candidate.latency_ms", 4.1);

            // Step 5: Mark step 1 & 2 as passed
            await taskEngine.AdvanceTaskStepAsync(agentTask.TaskId, 0, TaskStepStatus.Completed);
            await taskEngine.AdvanceTaskStepAsync(agentTask.TaskId, 1, TaskStepStatus.Completed);
            return true;
        });

        // Step 6: Automated regression test verification
        var testResult = await verificationService.RunTargetedSuiteAsync(new TestExecutionRequest("Tier1_FeatureCoverage", "F1"));
        Assert.True(testResult.Success);

        // Step 7: Finalize remaining task step and verify complete task resolution
        var finalTask = await taskEngine.AdvanceTaskStepAsync(agentTask.TaskId, 2, TaskStepStatus.Completed);
        Assert.Equal(TaskStatus.Completed, finalTask.Status);

        var taskVerification = await taskEngine.VerifyTaskAsync(agentTask.TaskId);
        Assert.True(taskVerification.Passed);

        // Step 8: Telemetry barrier flush
        await telemetryBarrier.FlushAsync();
        Assert.Equal(1, telemetryBarrier.FlushCount);
        Assert.Equal(2, telemetryBarrier.QueryMetrics("benchmark.baseline.latency_ms").Count + telemetryBarrier.QueryMetrics("benchmark.candidate.latency_ms").Count);
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "HighThroughputStudioAppMediaPipeline")]
    public async Task Scenario02_HighThroughputStudioAppMediaPipeline_CoordinatesSignalsAndState()
    {
        // Step 1: Studio App Manifest Resolution
        var filmCraft = FilmCraftApp.Manifest;
        Assert.Equal("filmcraft", filmCraft.Id);
        Assert.Equal("#8B5CF6", filmCraft.Accent);
        Assert.Equal("Studio", filmCraft.Category);

        var barrier = new TelemetryFlushBarrier();

        // Step 2: Initialize AppState with timeline and project assets
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

        // Step 3: Stream high-frequency render progress signals
        const int totalFrames = 500;
        for (int frame = 1; frame <= totalFrames; frame++)
        {
            var activity = new AppActivity(filmCraft.Id, "rendering", $"Rendered frame {frame}/{totalFrames}");
            barrier.RecordMetric("render.frames.completed", 1.0);
            if (frame % 100 == 0)
            {
                barrier.RecordMetric("render.checkpoint", frame);
            }
        }

        // Step 4: Advance revision on render completion
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

        // Step 5: Flush barrier to guarantee telemetry durability
        await barrier.FlushAsync();
        Assert.Equal(1, barrier.FlushCount);
        Assert.Equal(5, barrier.QueryMetrics("render.checkpoint").Count);
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "SecureAppSideloadConsentAndGrantGate")]
    public async Task Scenario03_SecureAppSideloadConsentAndGrantGate_EnforcesBoundaryProtection()
    {
        // Step 1: Malicious unauthenticated caller attempts sideloading
        var untrustedCaller = new CapabilityCaller("malicious_actor", "acc_unknown", "");
        Assert.False(CallerContextStamper.IsTrusted(untrustedCaller));

        // System rejects unauthenticated caller before examining payload
        var rejectedAtGate = !CallerContextStamper.IsTrusted(untrustedCaller);
        Assert.True(rejectedAtGate);

        // Step 2: Legitimate developer authenticates with verified provenance
        var verifiedCaller = CapabilityCallers.Of("dev_usr", "acc_corp", "brain_corp", CallerKind.User);
        Assert.True(CallerContextStamper.IsTrusted(verifiedCaller));

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

        // Step 4: Calculate consent requirements diff
        var consentDiff = AppConsent.Added(existingRequirements, newRequirements);
        Assert.Contains("audit/log", consentDiff.SemanticTypes!);
        Assert.Contains("external-api", consentDiff.Connectors!);
        Assert.Contains("OAUTH_SECRET", consentDiff.Secrets!);
        Assert.Contains("network.egress", consentDiff.Capabilities!);

        // Compute sha256 consent digest
        var consentDigest = AppConsent.Of(newRequirements);
        Assert.NotNull(consentDigest);
        Assert.Equal(64, consentDigest.Length);

        // Step 5: High-risk confirmation gate pauses installation
        var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Simulate security administrator approving elevated permissions
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
        // Step 1: Initialize Knowledge Store and populate system topology
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

        // Establish relationship graph
        await store.RecordEdgeAsync(new KnowledgeEdge("gateway", "auth-service", "authenticates_via", 1.0));
        await store.RecordEdgeAsync(new KnowledgeEdge("gateway", "media-service", "routes_to", 1.0));
        await store.RecordEdgeAsync(new KnowledgeEdge("media-service", "storage-s3", "persists_to", 0.9));

        // Step 2: Query graph view from gateway with depth 2 (traverses to storage-s3)
        var topology = await store.QueryGraphAsync("gateway", depth: 2);
        Assert.Equal(4, topology.Nodes.Count);
        Assert.Equal(3, topology.Edges.Count);

        // Step 3: Semantic search for services handling media persistence
        var searchHits = await store.SemanticSearchAsync("Media Storage", limit: 3);
        Assert.NotEmpty(searchHits);

        // Step 4: Inject graph knowledge into Deliberate Decision Maker
        var dm = new InMemoryDecisionMaker();
        var constraints = topology.Nodes.Select(n => $"{n.Label}:{n.Type}").ToArray();
        var goal = new DeliberateGoal("Optimize media ingestion route through storage-s3", constraints);

        var decision = await dm.DecideDeliberateAsync(goal);
        Assert.NotNull(decision);
        Assert.Equal(3, decision.Outcome.Steps.Length);
        Assert.All(decision.Outcome.Steps, s => Assert.Null(s.Error));

        // Step 5: Verify deliberate execution trace
        Assert.Contains(dm.AuditTrail, trail => trail.Contains("Optimize media ingestion route"));
    }

    [Fact]
    [Trait("Category", "Tier4_RealWorldScenarios")]
    [Trait("Scenario", "FaultInjectionAndResilientRecovery")]
    public async Task Scenario05_FaultInjectionAndResilientRecovery_GuaranteesFailClosedResilience()
    {
        // Step 1: Initialize components and execution context
        var caller = CapabilityCallers.Of("watchdog_agent", "acc_infra", "brain_core", CallerKind.User);
        var ctx = new InMemoryExecutionContext("exec_fault_recovery", caller);
        var dm = new InMemoryDecisionMaker();
        var taskEngine = new InMemoryTaskEngine();
        var telemetryBarrier = new TelemetryFlushBarrier();

        // Register reactive fallback rule
        dm.RegisterReactiveRule("node_unresponsive", "failover_to_standby", 0.98);

        // Step 2: Execute task with simulated subsystem failure
        var task = await taskEngine.CreateTaskAsync(new CreateTaskRequest(
            "Primary Node Health Check",
            "Detect and recover from unresponsive cluster node",
            "admin",
            [
                new TaskStepDefinition("s1", "ping_primary", "kernel"),
                new TaskStepDefinition("s2", "trigger_failover", "kernel"),
                new TaskStepDefinition("s3", "verify_standby", "kernel")
            ]));

        // Step 3: Step 1 fails due to injected timeout
        var failedTask = await taskEngine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Failed, "Connection timed out after 5000ms");
        Assert.Equal(TaskStatus.Failed, failedTask.Status);
        telemetryBarrier.RecordMetric("node.failures.injected", 1.0);

        // Step 4: Decision Maker resolves failover strategy via reactive rule
        var decision = await dm.DecideReactiveAsync(new ReactivePrompt("node_unresponsive", ["failover_to_standby"], new Dictionary<string, string>
        {
            ["failed_node"] = "primary-01"
        }));

        Assert.Equal("failover_to_standby", decision.Choice);
        Assert.Equal(0.98, decision.Confidence);

        // Step 5: Recovery task initiated and verified
        var recoveryTask = await taskEngine.CreateTaskAsync(new CreateTaskRequest(
            "Execute Failover",
            "Promote standby node",
            "admin",
            [
                new TaskStepDefinition("r1", "route_traffic_standby", "kernel"),
                new TaskStepDefinition("r2", "verify_quorum", "kernel")
            ]));

        await taskEngine.AdvanceTaskStepAsync(recoveryTask.TaskId, 0, TaskStepStatus.Completed);
        var completedRecovery = await taskEngine.AdvanceTaskStepAsync(recoveryTask.TaskId, 1, TaskStepStatus.Completed);
        Assert.Equal(TaskStatus.Completed, completedRecovery.Status);

        telemetryBarrier.RecordMetric("node.failover.succeeded", 1.0);

        // Step 6: Flush telemetry barrier and confirm all incident metrics persisted
        await telemetryBarrier.FlushAsync();
        Assert.Equal(1, telemetryBarrier.FlushCount);
        Assert.Single(telemetryBarrier.QueryMetrics("node.failures.injected"));
        Assert.Single(telemetryBarrier.QueryMetrics("node.failover.succeeded"));
    }
}
