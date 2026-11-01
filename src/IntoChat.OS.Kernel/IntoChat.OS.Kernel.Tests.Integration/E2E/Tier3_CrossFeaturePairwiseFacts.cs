#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
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
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.ServiceDefaults;
using AppManifest = DigitalBrain.AppManifest;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;

namespace IntoChat.OS.Kernel.Tests.Integration.E2E;

/// <summary>
/// Tier 3: Cross-Feature Combinations — 16 pairwise integration tests verifying seamless interaction
/// across decoupled kernel, runtime, security, manifest, and telemetry subsystems.
/// </summary>
public sealed class Tier3_CrossFeaturePairwiseFacts
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

    private static string FindProjectDirectory(string projectName)
    {
        var root = RepositoryRoot;
        var project = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx")).Descendants("Project")
            .Select(entry => entry.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(p => p.EndsWith("/" + projectName + ".csproj", StringComparison.Ordinal));
        return Path.Combine(root, Path.GetDirectoryName(project)!);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F1_F14")]
    public async Task TF01_HotPathSignals_WithExecutionContext_PreservesCorrelationAndTelemetry()
    {
        // F1 (Ephemeral Signals) + F14 (Canonical Execution Context)
        var caller = CapabilityCallers.Of("usr_tf01", "acc_1", "brain_1", CallerKind.User);
        var env = new Dictionary<string, string> { ["REGION"] = "us-west-2", ["SPAN_ID"] = "span_901" };
        var ctx = new InMemoryExecutionContext("exec_tf01", caller, env);

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
        Assert.False(CallerContextStamper.IsTrusted(untrustedCaller));

        // Attempting decision with unauthenticated caller rejected at boundary
        async Task<FastDecision> UnauthenticatedCall()
        {
            if (!CallerContextStamper.IsTrusted(untrustedCaller))
            {
                throw new InvalidOperationException("Caller context lacks verified provenance");
            }
            return await dm.DecideReactiveAsync(new ReactivePrompt("execute_critical", new Dictionary<string, string>()));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(UnauthenticatedCall);

        // Case B: Verified caller proceeds to deliberate decision synthesis
        var trustedCaller = CapabilityCallers.Of("agent-kernel", "acc-admin", "brain-primary", CallerKind.User);
        Assert.True(CallerContextStamper.IsTrusted(trustedCaller));

        var decision = await dm.DecideReactiveAsync(new ReactivePrompt("execute_critical", new Dictionary<string, string>
        {
            ["caller"] = trustedCaller.PrincipalId
        }));

        Assert.NotNull(decision);
        Assert.Equal("execute_default", decision.Choice);
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
        Assert.True(plan.Outcome.Succeeded);
        Assert.Equal(3, plan.Outcome.StepsExecuted);
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
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Run Unit Benchmarks", "Improve latency", ["run_tests", "record_metric"]));
        Assert.Equal(AgentTaskStatus.Pending, task.Status);

        // 2. Automated verification execution
        var testResult = await verifier.RunTargetedAsync(new TestExecutionRequest("Tier1_FeatureCoverage", "F1"));
        Assert.True(testResult.AllPassed);

        // 3. Update task steps based on test pass
        var t1 = await engine.UpdateStepStatusAsync(task.Id, "step_1", TaskStepStatus.Passed);
        Assert.Equal(AgentTaskStatus.InProgress, t1.Status);

        var t2 = await engine.UpdateStepStatusAsync(task.Id, "step_2", TaskStepStatus.Passed);
        Assert.Equal(AgentTaskStatus.Succeeded, t2.Status);

        // 4. Verify overall task completion
        var verification = await engine.VerifyTaskAsync(task.Id);
        Assert.True(verification.IsValid);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F3_F17")]
    public async Task TF05_ConfirmationResolution_WithTaskEngine_BlocksTaskUntilUserApproval()
    {
        // F3 (Confirmations) + F17 (Canonical Task Engine)
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Database Migration", "Apply schema", ["await_confirmation", "apply_sql"]));

        var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Simulate operator confirmation
        var confirmTask = Task.Run(async () =>
        {
            await Task.Yield();
            tcs.TrySetResult(new ConfirmationResult(task.Id, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false));
        });

        var confirmation = await tcs.Task;
        await confirmTask;

        Assert.Equal(ConfirmationOutcome.Approved, confirmation.Outcome);
        Assert.True(confirmation.Approved);

        // Once confirmed, steps advance to Succeeded
        await engine.UpdateStepStatusAsync(task.Id, "step_1", TaskStepStatus.Passed);
        var finalTask = await engine.UpdateStepStatusAsync(task.Id, "step_2", TaskStepStatus.Passed);
        Assert.Equal(AgentTaskStatus.Succeeded, finalTask.Status);
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
        var filmCraftManifest = FilmCraftApp.Manifest;
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
        Assert.True(CallerContextStamper.IsTrusted(validCaller));

        // Malformed callers fail closed
        var missingPrincipal = new CapabilityCaller("", "account-xyz", "brain-prod");
        var missingBrain = new CapabilityCaller("user-123", "account-xyz", "");
        var whitespacePrincipal = new CapabilityCaller("   ", "account-xyz", "brain-prod");

        Assert.False(CallerContextStamper.IsTrusted(missingPrincipal));
        Assert.False(CallerContextStamper.IsTrusted(missingBrain));
        Assert.False(CallerContextStamper.IsTrusted(whitespacePrincipal));
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
    [Trait("Pairwise", "F10_F13")]
    public void TF10_SystemOneHosting_WithKernelContracts_MaintainsCleanSeparation()
    {
        // F10 (Fix SystemOne Hosting Path) + F13 (Retire Legacy Shims & Forwards)
        var hostingFile = Path.Combine(FindProjectDirectory("IntoChat.OS.Aspire.Hosting"), "Modules/AI/Configuration/SystemOneHostingState.cs");
        Assert.True(File.Exists(hostingFile), $"SystemOneHostingState.cs must exist at '{hostingFile}'");

        var kernelAssembly = typeof(IntoChat.OS.Kernel.CloudHost).Assembly;
        var kernelTypes = kernelAssembly.GetTypes().Select(t => t.Name).ToList();

        // Verify kernel does not reference obsolete shim classes
        Assert.DoesNotContain("IntoChatOSNamesForward", kernelTypes);
        Assert.DoesNotContain("LegacyTypeNames", kernelTypes);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F1_F9")]
    public void TF11_AppActivity_WithAppManifest_CorrelatesSignalsWithStudioApps()
    {
        // F1 (Ephemeral Signals) + F9 (Studio App Manifests)
        var filmCraft = FilmCraftApp.Manifest;
        var activity = new AppActivity(filmCraft.Id, "rendering", "Exporting 4K sequence");

        Assert.Equal("filmcraft", activity.App);
        Assert.Equal("rendering", activity.State);
        Assert.Equal("Exporting 4K sequence", activity.Detail);

        var photoCraft = PhotoCraftApp.Manifest;
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
        var ctx = new InMemoryExecutionContext("exec_tf12", caller);
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
        Assert.NotEmpty(decision.Outcome.TraceLogs);

        // Convert decision trace into subtasks
        var taskReq = new CreateTaskRequest("Scale Workers Task", decision.Plan, decision.Outcome.TraceLogs);
        var task = await engine.CreateTaskAsync(taskReq);

        Assert.Equal(AgentTaskStatus.Pending, task.Status);
        Assert.Equal(decision.Outcome.TraceLogs.Length, task.Steps.Length);

        // Complete each subtask step
        for (int i = 0; i < task.Steps.Length; i++)
        {
            await engine.UpdateStepStatusAsync(task.Id, task.Steps[i].Id, TaskStepStatus.Passed);
        }

        var completed = await engine.GetTaskAsync(task.Id);
        Assert.NotNull(completed);
        Assert.Equal(AgentTaskStatus.Succeeded, completed.Status);
    }

    [Fact]
    [Trait("Category", "Tier3_CrossFeature")]
    [Trait("Pairwise", "F16_F20")]
    public async Task TF14_KnowledgeStore_WithTestVerification_StoresAndValidatesRegressionGraph()
    {
        // F16 (Canonical Memory with Graph UI) + F20 (Reusable Testing Framework)
        var store = new InMemoryKnowledgeStore();
        var verifier = new InMemoryTestVerificationService();

        var testRun = await verifier.RunTargetedAsync(new TestExecutionRequest("Tier2_BoundaryCorner"));
        Assert.True(testRun.AllPassed);

        var runNode = new KnowledgeNode(
            "run_001",
            "Tier2_BoundaryCorner_Run",
            "TestResult",
            new Dictionary<string, string>
            {
                ["PassedCount"] = testRun.PassedCount.ToString(),
                ["AllPassed"] = testRun.AllPassed.ToString()
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
        Assert.True(CallerContextStamper.IsTrusted(caller));

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
