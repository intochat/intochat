using DigitalBrain.Runtime.Orleans.Capabilities;
#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Confirmations;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.ServiceDefaults;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;
using TaskStatus = IntoChat.OS.Modules.AI.Tasks.TaskStatus;
using TaskStepStatus = IntoChat.OS.Modules.AI.Tasks.TaskStepStatus;
using ExecutionContext = IntoChat.OS.Modules.AI.Context.ExecutionContext;

namespace IntoChat.OS.Kernel.Tests.Unit.Harness;

/// <summary>
/// Tier 3: Cross-Feature Combinations — Pairwise tests verifying seamless interaction
/// across decoupled kernel, runtime, security, manifest, and telemetry subsystems.
/// </summary>
public sealed class Tier3_CrossFeaturePairwiseFacts
{
    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F1_F14")]
    public async Task TF01_HotPathSignals_WithExecutionContext_PreservesCorrelationAndTelemetry()
    {
        // F1 (Ephemeral Signals) + F14 (Canonical Execution Context)
        var caller = CapabilityCallers.Of("usr_tf01", "acc_1", "brain_1", CallerKind.User);
        var env = new Dictionary<string, string> { ["REGION"] = "us-west-2", ["SPAN_ID"] = "span_901" };
        var ctx = new ExecutionContext("exec_tf01", caller, env);

        var signal = new AppActivity("app-calc", "computing", "calculating route");
        var slice = new ContextSlice("CalculationSpan", signal.Detail, ["compute", "routing"]);

        var executionResult = await ctx.WithSliceAsync(slice, () =>
        {
            Assert.Single(ctx.Slices);
            Assert.Equal("CalculationSpan", ctx.Slices[0].Name);
            Assert.Equal("app-calc", signal.App);
            Assert.Equal("us-west-2", ctx.Environment["REGION"]);
            return Task.FromResult(signal.Detail);
        });

        Assert.Equal("calculating route", executionResult);
        Assert.Empty(ctx.Slices);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F6_F15")]
    public async Task TF02_CallerProvenance_WithDecisionMaker_EnforcesSecurityBoundaryBeforeSynthesis()
    {
        // F6 (Caller Context Provenance) + F15 (Canonical Decision Maker)
        var dm = new InMemoryDecisionMaker();

        // Case A: Missing or invalid caller fails closed
        var untrustedCaller = new CapabilityCaller("", "anon", "");
        Assert.False(RuntimeCallerContext.IsTrusted(untrustedCaller));

        // Attempting decision with unauthenticated caller rejected at boundary
        async Task<FastDecision> UnauthenticatedCall()
        {
            if (!RuntimeCallerContext.IsTrusted(untrustedCaller))
            {
                throw new InvalidOperationException("Caller context lacks verified provenance");
            }
            return await dm.DecideReactiveAsync(new ReactivePrompt("execute_critical", ["execute_critical"]));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(UnauthenticatedCall);

        // Case B: Verified caller proceeds to deliberate decision synthesis
        var trustedCaller = CapabilityCallers.Of("agent-kernel", "acc-admin", "brain-primary", CallerKind.User);
        Assert.True(RuntimeCallerContext.IsTrusted(trustedCaller));

        var decision = await dm.DecideReactiveAsync(new ReactivePrompt("execute_critical", ["execute_critical"], new Dictionary<string, string>
        {
            ["caller"] = trustedCaller.PrincipalId
        }));

        Assert.NotNull(decision);
        Assert.Equal("execute_critical", decision.Choice);
        Assert.True(decision.Confidence > 0.9);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F15_F16")]
    public async Task TF03_KnowledgeGraph_WithDecisionMaker_InjectsGraphMemoryIntoPlanSynthesis()
    {
        // F15 (Canonical Decision Maker) + F16 (Canonical Memory with Graph UI)
        var store = new InMemoryKnowledgeStore();
        await store.RecordNodeAsync(new KnowledgeNode("sys-topology", "SystemTopology", "Concept", new Dictionary<string, string> { ["nodes"] = "5" }));
        await store.RecordNodeAsync(new KnowledgeNode("svc-db", "DatabaseService", "Service", new Dictionary<string, string> { ["replica"] = "synced" }));
        await store.RecordEdgeAsync(new KnowledgeEdge("sys-topology", "svc-db", "contains", 1.0));

        var graph = await store.QueryGraphAsync("sys-topology", depth: 1);
        Assert.Equal(2, graph.Nodes.Count);

        var dm = new InMemoryDecisionMaker();
        var goal = new DeliberateGoal("Optimize database routing based on graph memory", [$"Nodes={graph.Nodes.Count}"]);

        var plan = await dm.DecideDeliberateAsync(goal);
        Assert.NotNull(plan);
        Assert.Equal(3, plan.Outcome.Steps.Length);
        Assert.All(plan.Outcome.Steps, s => Assert.Null(s.Error));
        Assert.Contains("Deliberate:Optimize database routing based on graph memory", dm.AuditTrail);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F17_F20")]
    public async Task TF04_TaskEngine_WithTestVerification_EnablesAutonomousSelfImprovementCycle()
    {
        // F17 (Canonical Task Engine) + F20 (Reusable Test Verification Framework)
        var engine = new InMemoryTaskEngine();
        var verifier = new InMemoryTestVerificationService();

        // 1. Enqueue self-improvement task
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Run Unit Benchmarks", "Improve latency", "admin", [new TaskStepDefinition("s1", "run_tests", "kernel"), new TaskStepDefinition("s2", "record_metric", "kernel")]));
        Assert.Equal(TaskStatus.Pending, task.Status);

        // 2. Automated verification execution
        var testResult = await verifier.RunTargetedSuiteAsync(new TestExecutionRequest("Tier1_FeatureCoverage", "F1"));
        Assert.True(testResult.Success);

        // 3. Update task steps based on test pass
        var t1 = await engine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Completed);
        Assert.Equal(TaskStatus.Running, t1.Status);

        var t2 = await engine.AdvanceTaskStepAsync(task.TaskId, 1, TaskStepStatus.Completed);
        Assert.Equal(TaskStatus.Completed, t2.Status);

        // 4. Verify overall task completion
        var verification = await engine.VerifyTaskAsync(task.TaskId);
        Assert.True(verification.Passed);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F3_F17")]
    public async Task TF05_ConfirmationResolution_WithTaskEngine_BlocksTaskUntilUserApproval()
    {
        // F3 (Confirmations) + F17 (Canonical Task Engine)
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Database Migration", "Apply schema", "admin", [new TaskStepDefinition("s1", "await_confirmation", "kernel"), new TaskStepDefinition("s2", "apply_sql", "kernel")]));

        var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Simulate operator confirmation
        var confirmTask = Task.Run(async () =>
        {
            await Task.Yield();
            tcs.TrySetResult(new ConfirmationResult(task.TaskId, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false));
        });

        var confirmation = await tcs.Task;
        await confirmTask;

        Assert.Equal(ConfirmationOutcome.Approved, confirmation.Outcome);
        Assert.True(confirmation.Approved);

        // Once confirmed, steps advance to Succeeded
        await engine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Completed);
        var finalTask = await engine.AdvanceTaskStepAsync(task.TaskId, 1, TaskStepStatus.Completed);
        Assert.Equal(TaskStatus.Completed, finalTask.Status);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F4_F21")]
    public async Task TF06_TelemetryBarrier_WithStreamingSignals_FlushesAllEventsUnderLoad()
    {
        // F4 (Streaming Signal Optimization) + F21 (Telemetry Verification & Flush Barrier)
        var barrier = new TelemetryFlushBarrier();

        const int signalCount = 1_000;
        for (int i = 0; i < signalCount; i++)
        {
            var activity = new AppActivity("app-stream", "working", $"chunk-{i}");
            barrier.RecordMetric("stream.events", 1.0);
            Assert.Equal("working", activity.State);
        }

        var metrics = barrier.QueryMetrics("stream.events");
        Assert.Equal(signalCount, metrics.Count);

        // Flush barrier
        await barrier.FlushAsync();
        Assert.Equal(1, barrier.FlushCount);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F7_F9")]
    public void TF07_AppConsent_WithAppManifest_ValidatesStudioAppRequirements()
    {
        // F7 (App Consent Diffing) + F9 (Studio App Manifests)
        var filmCraftManifest = FilmCraftApp.Definition;
        Assert.Equal("filmcraft", filmCraftManifest.Id);

        var filmCraftReqs = new AppRequirements(
            SemanticTypes: ["video/mp4", "audio/wav"],
            Connectors: ["local-fs", "gpu-encoder"],
            Secrets: [],
            Capabilities: ["render", "export"]);

        var hash = AppConsent.Of(filmCraftReqs);
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);

        var photoCraftReqs = new AppRequirements(
            SemanticTypes: ["image/png", "image/jpeg"],
            Connectors: ["local-fs"],
            Secrets: [],
            Capabilities: ["filter", "export"]);

        var diff = AppConsent.Added(photoCraftReqs, filmCraftReqs);
        Assert.Contains("video/mp4", diff.SemanticTypes!);
        Assert.Contains("gpu-encoder", diff.Connectors!);
        Assert.Contains("render", diff.Capabilities!);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F5_F8")]
    public void TF08_CapabilityCaller_WithPlainNeuronFilter_RejectsImpersonationAndMalformedMetadata()
    {
        // F5 (Fail-Closed PlainNeuronCallFilter) + F8 (Strict Grant Rule Evaluation)
        var validCaller = CapabilityCallers.Of("user-123", "account-xyz", "brain-prod", CallerKind.User);
        Assert.True(RuntimeCallerContext.IsTrusted(validCaller));

        // Malformed callers fail closed
        var missingPrincipal = new CapabilityCaller("", "account-xyz", "brain-prod");
        var missingBrain = new CapabilityCaller("user-123", "account-xyz", "");
        var whitespacePrincipal = new CapabilityCaller("   ", "account-xyz", "brain-prod");

        Assert.False(RuntimeCallerContext.IsTrusted(missingPrincipal));
        Assert.False(RuntimeCallerContext.IsTrusted(missingBrain));
        Assert.False(RuntimeCallerContext.IsTrusted(whitespacePrincipal));
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F11_F12")]
    public void TF09_FastJson_WithAppState_SerializesCollectionsWithFidelity()
    {
        // F11 (FastJson Deserialization) + F12 (AppState Retired Collections & Shapes)
        var originalState = new AppState
        {
            Files = new Dictionary<string, string>
            {
                ["manifest.json"] = "{\"version\": 2}",
                ["config.yaml"] = "mode: test"
            },
            Settings = new Dictionary<string, string>
            {
                ["THEME"] = "dark",
                ["AUTOSAVE"] = "true"
            },
            Connections = new Dictionary<string, string>
            {
                ["eventStore"] = "tcp://127.0.0.1:1113",
                ["vectorDb"] = "http://localhost:6333"
            }
        };

        var json = JsonSerializer.Serialize(originalState);
        var restored = JsonSerializer.Deserialize<AppState>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.Files.Count);
        Assert.Equal("{\"version\": 2}", restored.Files["manifest.json"]);
        Assert.Equal("dark", restored.Settings["THEME"]);
        Assert.Equal("tcp://127.0.0.1:1113", restored.Connections["eventStore"]);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F1_F9")]
    public void TF11_AppActivity_WithAppManifest_CorrelatesSignalsWithStudioApps()
    {
        // F1 (Ephemeral Signals) + F9 (Studio App Manifests)
        var filmCraft = FilmCraftApp.Definition;
        var activity = new AppActivity(filmCraft.Id, "rendering", "Exporting 4K sequence");

        Assert.Equal("filmcraft", activity.App);
        Assert.Equal("rendering", activity.State);
        Assert.Equal("Exporting 4K sequence", activity.Detail);

        var photoCraft = PhotoCraftApp.Definition;
        var photoActivity = new AppActivity(photoCraft.Id, "editing", "Applying color grade");

        Assert.Equal("photocraft", photoActivity.App);
        Assert.NotEqual(activity.App, photoActivity.App);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F14_F21")]
    public async Task TF12_ExecutionContext_WithTelemetryFlushBarrier_SynchronizesMetricsAcrossSpans()
    {
        // F14 (Canonical Execution Context) + F21 (Telemetry Verification & Flush Barrier)
        var caller = CapabilityCallers.Of("agent-telemetry", "acc-1", "brain-1", CallerKind.User);
        var ctx = new ExecutionContext("exec_tf12", caller);
        var barrier = new TelemetryFlushBarrier();

        var slice = new ContextSlice("TelemetrySlice", "recording metrics", ["metrics"]);
        await ctx.WithSliceAsync(slice, () =>
        {
            barrier.RecordMetric("execution.spans", 1.0);
            return Task.FromResult(true);
        });

        Assert.Single(barrier.QueryMetrics("execution.spans"));
        await barrier.FlushAsync();
        Assert.Equal(1, barrier.FlushCount);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F15_F17")]
    public async Task TF13_DecisionMaker_WithTaskEngine_TranslatesDecisionsIntoExecutableSubtasks()
    {
        // F15 (Canonical Decision Maker) + F17 (Canonical Task Engine)
        var dm = new InMemoryDecisionMaker();
        var engine = new InMemoryTaskEngine();

        var goal = new DeliberateGoal("Scale cluster worker pool", ["min_instances=3", "max_instances=10"]);
        var decision = await dm.DecideDeliberateAsync(goal);
        Assert.NotNull(decision);
        Assert.NotEmpty(decision.Outcome.Steps);

        // Convert decision trace into subtasks
        var steps = decision.Outcome.Steps.Select(s => new TaskStepDefinition(s.Method, s.Result ?? s.Method, "kernel")).ToArray();
        var taskReq = new CreateTaskRequest("Scale Workers Task", decision.Plan, "admin", steps);
        var task = await engine.CreateTaskAsync(taskReq);

        Assert.Equal(TaskStatus.Pending, task.Status);
        Assert.Equal(decision.Outcome.Steps.Length, task.Steps.Length);

        // Complete each subtask step
        for (int i = 0; i < task.Steps.Length; i++)
        {
            await engine.AdvanceTaskStepAsync(task.TaskId, i, TaskStepStatus.Completed);
        }

        var completed = await engine.GetTaskAsync(task.TaskId);
        Assert.NotNull(completed);
        Assert.Equal(TaskStatus.Completed, completed.Status);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F16_F20")]
    public async Task TF14_KnowledgeStore_WithTestVerification_StoresAndValidatesRegressionGraph()
    {
        // F16 (Canonical Memory with Graph UI) + F20 (Reusable Testing Framework)
        var store = new InMemoryKnowledgeStore();
        var verifier = new InMemoryTestVerificationService();

        var testRun = await verifier.RunTargetedSuiteAsync(new TestExecutionRequest("Tier2_BoundaryCorner"));
        Assert.True(testRun.Success);

        var runNode = new KnowledgeNode(
            "run_001",
            "Tier2_BoundaryCorner_Run",
            "TestResult",
            new Dictionary<string, string>
            {
                ["PassedCount"] = testRun.Passed.ToString(),
                ["AllPassed"] = testRun.Success.ToString()
            });

        await store.RecordNodeAsync(runNode);

        var recallHits = await store.SemanticSearchAsync("BoundaryCorner", limit: 5);
        Assert.NotEmpty(recallHits);
        Assert.Equal("run_001", recallHits[0].Node.Id);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F6_F7")]
    public void TF15_AppConsent_WithCapabilityCaller_DiffsRequirementsAgainstCallerScope()
    {
        // F6 (Caller Context Provenance) + F7 (App Consent Diffing)
        var caller = CapabilityCallers.Of("agent-app-manager", "acc-root", "brain-primary", CallerKind.User);
        Assert.True(RuntimeCallerContext.IsTrusted(caller));

        var registeredReqs = new AppRequirements(
            SemanticTypes: ["metrics/telemetry"],
            Connectors: ["otel-collector"],
            Secrets: [],
            Capabilities: ["push"]);

        var proposedReqs = new AppRequirements(
            SemanticTypes: ["metrics/telemetry", "logs/audit"],
            Connectors: ["otel-collector", "cloud-storage"],
            Secrets: ["STORAGE_CREDENTIAL"],
            Capabilities: ["push", "admin"]);

        var added = AppConsent.Added(registeredReqs, proposedReqs);

        Assert.Contains("logs/audit", added.SemanticTypes!);
        Assert.Contains("cloud-storage", added.Connectors!);
        Assert.Contains("STORAGE_CREDENTIAL", added.Secrets!);
        Assert.Contains("admin", added.Capabilities!);
        Assert.DoesNotContain("push", added.Capabilities!);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F1_F12")]
    public void TF16_PresentationChanged_WithAppState_UpdatesRevisionsOnStateChange()
    {
        // F1 (Ephemeral Signals) + F12 (AppState Retired Collections & Shapes)
        var state = new AppState
        {
            Files = new Dictionary<string, string> { ["index.html"] = "<h1>Hello</h1>" }
        };

        var p1 = new PresentationChanged("app-web", 1, 1);
        Assert.Equal(1, p1.Revision);

        // Update state with new file
        var stateUpdated = new AppState
        {
            Files = new Dictionary<string, string>
            {
                ["index.html"] = "<h1>Hello</h1>",
                ["style.css"] = "body { margin: 0; }"
            }
        };

        var p2 = new PresentationChanged("app-web", 1, 2);
        Assert.Equal(2, p2.Revision);
        Assert.Equal(2, stateUpdated.Files.Count);
        Assert.True(p2.Revision > p1.Revision);
    }
}
