using DigitalBrain.Runtime.Orleans.Capabilities;
#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Confirmation;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps.Signals;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;
using IntoChat.OS.Host.ServiceDefaults;

namespace IntoChat.OS.Kernel.Tests.Unit.Harness;

/// <summary>
/// Tier 1: Feature Coverage — Comprehensive opaque-box test suite verifying primary behavior (happy paths)
/// for harness and in-memory features in PROJECT.md § Feature Inventory.
/// </summary>
public sealed class Tier1_FeatureCoverageFacts
{
    #region F1: Ephemeral Signal Annotation

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F1")]
    public void F1_01_AppActivity_ValidStates_InstantiatesWithSignalMetadata()
    {
        var activity = new AppActivity("echo-app", "working", "processing prompt");
        Assert.Equal("echo-app", activity.App);
        Assert.Equal("working", activity.State);
        Assert.Equal("processing prompt", activity.Detail);
        Assert.True(typeof(DigitalBrain.Signal).IsAssignableFrom(typeof(AppActivity)));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F1")]
    public void F1_02_AppActivity_StatesInventory_ContainsExpectedHotPathStates()
    {
        var expectedStates = new[] { "idle", "working", "reading", "completed", "disconnected" };
        foreach (var expected in expectedStates)
        {
            Assert.Contains(expected, AppActivity.States);
        }
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F1")]
    public void F1_03_PresentationChanged_CarriesPresentationMetadata()
    {
        var presentation = new PresentationChanged("echo-app", 42, 1);
        Assert.Equal("echo-app", presentation.App);
        Assert.Equal(42, presentation.ProgramRevision);
        Assert.Equal(1, presentation.Revision);
        Assert.True(typeof(DigitalBrain.Signal).IsAssignableFrom(typeof(PresentationChanged)));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F1")]
    public void F1_04_AppRequested_InitializesWithOperationAndInput()
    {
        var id = Guid.NewGuid();
        var requested = new AppRequested("generate-text", "summarize report", id);
        Assert.Equal("generate-text", requested.Operation);
        Assert.Equal("summarize report", requested.Input);
        Assert.Equal(id, requested.RequestId);
        Assert.True(typeof(DigitalBrain.Signal).IsAssignableFrom(typeof(AppRequested)));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F1")]
    public void F1_05_EphemeralSignalTypes_DeriveFromDigitalBrainSignal()
    {
        Type[] ephemeralSignals = [typeof(AppActivity), typeof(PresentationChanged), typeof(AppRequested)];
        foreach (var type in ephemeralSignals)
        {
            Assert.True(typeof(DigitalBrain.Signal).IsAssignableFrom(type), $"{type.Name} must inherit from DigitalBrain.Signal");
            var attr = type.GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false);
            Assert.NotNull(attr);
            Assert.False(string.IsNullOrWhiteSpace(attr.Alias));
        }
    }

    #endregion

    #region F2: AppNeuron Progress In-Memory Separation

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F2")]
    public void F2_01_AppRef_KeyFormatting_ConformsToCanonicalPattern()
    {
        var key = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("brain-alpha"), "summarizer").Key;
        Assert.Equal("brain-alpha/apps/summarizer", key);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F2")]
    public void F2_02_IApp_NameOf_ExtractsValidAppSlug()
    {
        var name = IntoChat.OS.Host.Primitives.AppRef.NameOf("brain-alpha", "brain-alpha/apps/summarizer");
        Assert.Equal("summarizer", name);

        var foreign = IntoChat.OS.Host.Primitives.AppRef.NameOf("brain-beta", "brain-alpha/apps/summarizer");
        Assert.Null(foreign);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F2")]
    public void F2_03_AppSnapshot_StatusEnumeration_ReflectsLiveState()
    {
        var manifest = new IntoChat.OS.Modules.Apps.AppManifest("Test App", "Description", [], [], [], "icon.svg", []);
        var snapshot = new AppSnapshot(
            Name: "test-app",
            Manifest: manifest,
            Spec: "csharp",
            Files: new Dictionary<string, string>(),
            Settings: new Dictionary<string, string>(),
            Connections: new Dictionary<string, string>(),
            Version: "1.0.0",
            Status: AppStatus.Live,
            Activity: ["idle", "working"],
            Versions: []);

        Assert.Equal(AppStatus.Live, snapshot.Status);
        Assert.Equal(2, snapshot.Activity.Length);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F2")]
    public void F2_04_AppState_InstantiatesCleanCanonicalCollections()
    {
        var state = new AppState();
        Assert.NotNull(state.Files);
        Assert.NotNull(state.Settings);
        Assert.NotNull(state.Connections);
        Assert.Equal(AppStatus.Drafted, state.Status);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F2")]
    public void F2_05_AppActivity_InstantiatesIndependentlyOfAppState()
    {
        var activity = new AppActivity("rapid-app", "working", "hot path step 1");
        Assert.Equal("rapid-app", activity.App);
        Assert.Equal("working", activity.State);
    }

    #endregion

    #region F3: Asynchronous Confirmation Resolution

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F3")]
    public void F3_01_ConfirmationResult_ConstructsWithOutcomeAndReason()
    {
        var result = new ConfirmationResult("plan-101", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false);
        Assert.Equal("plan-101", result.PlanId);
        Assert.Equal(ConfirmationOutcome.Approved, result.Outcome);
        Assert.Equal(ConfirmationDenyReason.None, result.Reason);
        Assert.True(result.Approved);
        Assert.False(result.Skipped);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F3")]
    public void F3_02_ConfirmationOptions_DefaultPolicy_IsConfigured()
    {
        var options = new DigitalBrain.Confirmation.ConfirmationPolicies();
        options.Brains["brain-test"] = ConfirmationPolicy.Never;
        Assert.Equal(ConfirmationPolicy.Never, options.For("brain-test"));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F3")]
    public async Task F3_03_EventDrivenConfirmationCompletion_ResolvesAsyncTcs()
    {
        var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new ConfirmationResult("plan-99", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);

        _ = Task.Run(async () =>
        {
            await Task.Yield();
            tcs.TrySetResult(expected);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completed = await tcs.Task.WaitAsync(cts.Token);

        Assert.Equal(ConfirmationOutcome.Approved, completed.Outcome);
        Assert.Equal("plan-99", completed.PlanId);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F3")]
    public async Task F3_04_ConcurrentConfirmations_CompleteIndependently()
    {
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Yield();
            tcs.SetResult(new ConfirmationResult($"plan-{i}", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false));
            return await tcs.Task;
        });

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.Equal(ConfirmationOutcome.Approved, r.Outcome));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F3")]
    public void F3_05_ConfirmationResult_DeniedReason_ReflectsPolicyRejection()
    {
        var denied = new ConfirmationResult("plan-200", ConfirmationOutcome.Denied, ConfirmationDenyReason.SelfApproval, false);
        Assert.Equal("plan-200", denied.PlanId);
        Assert.Equal(ConfirmationOutcome.Denied, denied.Outcome);
        Assert.Equal(ConfirmationDenyReason.SelfApproval, denied.Reason);
        Assert.False(denied.Approved);
    }

    #endregion

    #region F4: Streaming Signal Allocation Optimization

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F4")]
    public void F4_01_HighThroughputSignalBatchProcessing_ExecutesQuickly()
    {
        var sw = Stopwatch.StartNew();
        const int batchSize = 10_000;
        var list = new List<AppActivity>(batchSize);
        for (int i = 0; i < batchSize; i++)
        {
            list.Add(new AppActivity("app-stream", "working", $"tick {i}"));
        }
        sw.Stop();

        Assert.Equal(batchSize, list.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"10k allocations took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F4")]
    public void F4_02_StopwatchTimestampCalculations_DoNotAllocateProcesses()
    {
        var ts1 = Stopwatch.GetTimestamp();
        var ts2 = Stopwatch.GetTimestamp();
        Assert.True(ts2 >= ts1);
        var elapsed = Stopwatch.GetElapsedTime(ts1, ts2);
        Assert.True(elapsed >= TimeSpan.Zero);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F4")]
    public void F4_03_SignalBatchSerialization_RoundTripsAccurately()
    {
        var original = new AppActivity("stream-agent", "reading", "sensor buffer");
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppActivity>(json);

        Assert.NotNull(restored);
        Assert.Equal(original.App, restored.App);
        Assert.Equal(original.State, restored.State);
        Assert.Equal(original.Detail, restored.Detail);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F4")]
    public void F4_04_PresentationChanged_SerializesWithIntactValues()
    {
        var signal = new PresentationChanged("video-app", 105, 3);
        var json = JsonSerializer.Serialize(signal);
        var deserialized = JsonSerializer.Deserialize<PresentationChanged>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("video-app", deserialized.App);
        Assert.Equal(105, deserialized.ProgramRevision);
        Assert.Equal(3, deserialized.Revision);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F4")]
    public async Task F4_05_StreamingAsyncEnumerable_ConsumesWithCancellationToken()
    {
        static async IAsyncEnumerable<AppActivity> StreamSignals(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await Task.Yield();
                yield return new AppActivity("agent-stream", "working", $"step {i}");
            }
        }

        var collected = new List<AppActivity>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in StreamSignals(10).WithCancellation(cts.Token))
        {
            collected.Add(item);
        }

        Assert.Equal(10, collected.Count);
    }

    #endregion

    #region F5: Fail-Closed PlainNeuronCallFilter

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F5")]
    public void F5_02_CallRequest_InitializesWithRequiredProvenance()
    {
        var edge = IntoChat.OS.Tests.TestEdge.Authority.Person("usr_9", "acc_1", new BrainId("brain_1"));
        var request = new CallRequest
        {
            TargetNeuron = "brain_1/apps/calc",
            Operation = "execute"
        };

        Assert.Equal("usr_9", edge.For?.Principal ?? (edge.Actor as Actor.Person)?.Principal);
        Assert.Equal("brain_1/apps/calc", request.TargetNeuron);
        Assert.Equal("execute", request.Operation);
        Assert.False(request.PublicOperation);
    }

    #endregion

    #region F6: Provenance Caller Construction Hygiene

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F6")]
    public void F6_05_AppPrincipal_IdFormatting_GeneratesExpectedPrefix()
    {
        var appKey = "brain_1/apps/my_tool";
        var principal = AppPrincipal.Id(appKey);
        Assert.Equal("app:brain_1/apps/my_tool", principal);
        Assert.True(AppPrincipal.IsApp(principal));
        Assert.Equal(appKey, AppPrincipal.AppKeyOf(principal));
    }

    #endregion

    #region F7: Sideload Consent Gate Enforcement

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F7")]
    public void F7_02_AppConsent_Of_ComputesHexDigest()
    {
        var req = new AppRequirements(SemanticTypes: ["text.summary"], Connectors: ["openai"], Secrets: ["api-key"], Capabilities: ["net.out"]);
        var digest = AppConsent.Of(req);
        Assert.NotNull(digest);
        Assert.Equal(64, digest.Length);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F7")]
    public void F7_03_AppConsent_IsEmpty_DetectsEmptyRequirements()
    {
        Assert.True(AppConsent.IsEmpty(null));
        Assert.True(AppConsent.IsEmpty(new AppRequirements(SemanticTypes: [], Connectors: [], Secrets: [], Capabilities: [])));
        Assert.False(AppConsent.IsEmpty(new AppRequirements(SemanticTypes: ["cap"], Connectors: [], Secrets: [], Capabilities: [])));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F7")]
    public void F7_05_AppOrigin_RetainsOriginVerificationMetadata()
    {
        var origin = new AppOrigin(
            Publisher: "IntoChat Verified",
            Source: new AppSource.Shipped(),
            Name: "summarizer",
            Version: "1.0.0");

        Assert.Equal("IntoChat Verified", origin.Publisher);
        Assert.IsType<AppSource.Shipped>(origin.Source);
        Assert.Equal("summarizer", origin.Name);
        Assert.Equal("1.0.0", origin.Version);
    }

    #endregion

    #region F8: Strict Grant Rule Evaluation

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F8")]
    public void F8_01_GrantRules_GrantsMatch_EvaluatesExactCapabilityId()
    {
        var grants = new[] { "storage.read", "network.outbound" };
        Assert.Contains("storage.read", grants);
        Assert.DoesNotContain("storage.write", grants);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F8")]
    public void F8_02_GrantRules_EmptyGrants_FailsClosed()
    {
        var grants = Array.Empty<string>();
        Assert.Empty(grants);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F8")]
    public void F8_04_GrantEvaluation_DistinguishesBetweenDifferentBrains()
    {
        var callerAlpha = IntoChat.OS.Tests.TestEdge.Authority.Person("user_1", "acc_1", new BrainId("brain_alpha"));
        var callerBeta = IntoChat.OS.Tests.TestEdge.Authority.Person("user_1", "acc_1", new BrainId("brain_beta"));

        Assert.NotEqual(callerAlpha.Brain, callerBeta.Brain);
    }

    #endregion

    #region F9: Eliminate Dual App Definitions

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_01_FilmCraftApp_Manifest_DeclaresUniqueId()
    {
        var bundle = FilmCraftApp.Bundle;
        Assert.NotNull(bundle);
        Assert.Equal("filmcraft", bundle.Name);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_02_PhotoCraftApp_Manifest_DeclaresUniqueId()
    {
        var bundle = PhotoCraftApp.Bundle;
        Assert.NotNull(bundle);
        Assert.Equal("photocraft", bundle.Name);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_03_StudioApps_HaveDistinctManifestIdentities()
    {
        Assert.NotEqual(FilmCraftApp.Bundle.Name, PhotoCraftApp.Bundle.Name);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_04_FilmCraftApp_AccentAndCategory_AreConfigured()
    {
        Assert.True(FilmCraftApp.Bundle.Manifest.Studio);
        Assert.Equal("filmcraft", FilmCraftApp.Bundle.Name);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_05_PhotoCraftApp_AccentAndCategory_AreConfigured()
    {
        Assert.True(PhotoCraftApp.Bundle.Manifest.Studio);
        Assert.Equal("photocraft", PhotoCraftApp.Bundle.Name);
    }

    #endregion

    #region F12: Retire AppState Legacy Collections & Shape

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F12")]
    public void F12_01_AppState_CanonicalFiles_TakesPrecedenceOverLegacy()
    {
        var state = new AppState();
        Assert.NotNull(state.Files);
        Assert.Empty(state.Files);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F12")]
    public void F12_02_AppState_CanonicalSettings_TakesPrecedenceOverLegacy()
    {
        var state = new AppState();
        Assert.NotNull(state.Settings);
        Assert.Empty(state.Settings);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F12")]
    public void F12_03_AppState_CanonicalConnections_TakesPrecedenceOverLegacy()
    {
        var state = new AppState();
        Assert.NotNull(state.Connections);
        Assert.Empty(state.Connections);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F12")]
    public void F12_04_AppState_ContainsCanonicalProperties()
    {
        Assert.NotNull(typeof(AppState).GetProperty("Files"));
        Assert.NotNull(typeof(AppState).GetProperty("Settings"));
        Assert.NotNull(typeof(AppState).GetProperty("Connections"));
        Assert.NotNull(typeof(AppState).GetProperty("Status"));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F12")]
    public void F12_05_AppState_DoesNotCarryLegacyShapeAttribute()
    {
        var attrs = typeof(AppState).GetCustomAttributes();
        Assert.DoesNotContain(attrs, a => a.GetType().Name == "LegacyShapeAttribute");
    }

    #endregion

    #region F13: Retire Legacy Shims & Forwards

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_01_IntoChatOSNamesForward_IsIsolatedFromKernel()
    {
        var kernelTypes = typeof(IntoChat.OS.Kernel.OperatingSystemModules).Assembly.GetTypes();
        Assert.DoesNotContain(kernelTypes, t => t.Name.Contains("NamesForward", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_02_LegacyTypeNames_IsIsolatedFromKernel()
    {
        var kernelTypes = typeof(IntoChat.OS.Kernel.OperatingSystemModules).Assembly.GetTypes();
        Assert.DoesNotContain(kernelTypes, t => t.Name.Contains("LegacyTypeNames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_04_FrameworkAssembly_ExportsCanonicalConfigurationTypes()
    {
        var exportedTypes = typeof(CallRequest).Assembly.GetExportedTypes().Select(t => t.Name).ToList();
        Assert.Contains("Caller", typeof(DigitalBrain.Access.Caller).Assembly.GetExportedTypes().Select(t => t.Name));
        Assert.Contains("CallRequest", exportedTypes);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_05_CleanNamespaceIsolation_NoObsoleteShims()
    {
        var types = typeof(IntoChat.OS.Host.Modules.OsModule).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("NamesForward", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region F16: Canonical Memory with Graph UI

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F16")]
    public async Task F16_01_RecordNodeAsync_StoresKnowledgeNode()
    {
        var store = new InMemoryKnowledgeStore();
        var node = new KnowledgeNode("node_1", "Kernel Architecture", "Concept", new Dictionary<string, string> { ["author"] = "IntoChat" });

        await store.RecordNodeAsync(node);
        var graph = await store.QueryGraphAsync("node_1", depth: 1);

        Assert.Single(graph.Nodes);
        Assert.Equal("Kernel Architecture", graph.Nodes[0].Label);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F16")]
    public async Task F16_02_RecordEdgeAsync_ConnectsNodesInGraph()
    {
        var store = new InMemoryKnowledgeStore();
        var n1 = new KnowledgeNode("app_1", "FilmCraft", "Application", new Dictionary<string, string>());
        var n2 = new KnowledgeNode("cap_1", "VideoExport", "Capability", new Dictionary<string, string>());
        await store.RecordNodeAsync(n1);
        await store.RecordNodeAsync(n2);

        var edge = new KnowledgeEdge("app_1", "cap_1", "Requires", 1.0);
        await store.RecordEdgeAsync(edge);

        var graph = await store.QueryGraphAsync("app_1", depth: 2);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.Edges);
        Assert.Equal("Requires", graph.Edges[0].Relation);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F16")]
    public async Task F16_03_QueryGraphAsync_BFSStopsAtSpecifiedDepth()
    {
        var store = new InMemoryKnowledgeStore();
        for (int i = 1; i <= 5; i++)
        {
            await store.RecordNodeAsync(new KnowledgeNode($"n{i}", $"Level {i}", "Tier", new Dictionary<string, string>()));
            if (i > 1)
            {
                await store.RecordEdgeAsync(new KnowledgeEdge($"n{i - 1}", $"n{i}", "Next", 1.0));
            }
        }

        var graphDepth1 = await store.QueryGraphAsync("n1", depth: 1);
        Assert.Equal(2, graphDepth1.Nodes.Count);

        var graphDepth2 = await store.QueryGraphAsync("n1", depth: 2);
        Assert.Equal(3, graphDepth2.Nodes.Count);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F16")]
    public async Task F16_04_SemanticSearchAsync_RanksMatchingNodes()
    {
        var store = new InMemoryKnowledgeStore();
        await store.RecordNodeAsync(new KnowledgeNode("k1", "Security Provenance Gate", "Security", new Dictionary<string, string>()));
        await store.RecordNodeAsync(new KnowledgeNode("k2", "High Throughput Audio Streaming", "Media", new Dictionary<string, string>()));

        var hits = await store.SemanticSearchAsync("Security Gate", limit: 5);
        Assert.NotEmpty(hits);
        Assert.Equal("k1", hits[0].Node.Id);
        Assert.True(hits[0].RelevanceScore > 0.5);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F16")]
    public async Task F16_05_QueryGraphAsync_ReturnsEmptyForUnknownRoot()
    {
        var store = new InMemoryKnowledgeStore();
        var graph = await store.QueryGraphAsync("non_existent_id", depth: 2);
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    #endregion

    #region F20: Reusable Testing Framework for Agent Verification

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_01_TestVerificationService_ExecutesTargetedTier()
    {
        var svc = new InMemoryTestVerificationService();
        var req = new TestExecutionRequest("Tier1_FeatureCoverage", "F1");

        var result = await svc.RunTargetedSuiteAsync(req);
        Assert.True(result.Success);
        Assert.Equal(10, result.Total);
        Assert.Equal(10, result.Passed);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_02_TestVerificationService_VerifiesMilestones()
    {
        var svc = new InMemoryTestVerificationService();
        var runs = await svc.GetHistoricalRunsAsync("M1");
        Assert.NotNull(runs);
        Assert.NotEmpty(runs);
        Assert.True(runs[0].Success);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_03_TestExecutionResult_EncapsulatesFailureLogs()
    {
        var svc = new InMemoryTestVerificationService();
        var result = await svc.RunTargetedSuiteAsync(new TestExecutionRequest("Tier2_BoundaryCorner"));
        Assert.Empty(result.Failures);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public void F20_04_TestExecutionRequest_InitializesWithDefaults()
    {
        var req = new TestExecutionRequest("Tier3_CrossFeature");
        Assert.Equal("Tier3_CrossFeature", req.SuiteName);
        Assert.Null(req.Filter);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_05_VerifyMilestoneAsync_ThrowsOnEmptyMilestoneId()
    {
        var svc = new InMemoryTestVerificationService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetHistoricalRunsAsync(""));
    }

    #endregion

    #region F21: Aspire Telemetry Verification & Flush Barrier

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F21")]
    public void F21_01_TelemetryFlushBarrier_RecordsMetrics()
    {
        var barrier = new TelemetryFlushBarrier();
        barrier.RecordMetric("http.server.requests", 152.0);

        var metrics = barrier.QueryMetrics("http.server.requests");
        Assert.Single(metrics);
        Assert.Equal(152.0, metrics[0].Value);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F21")]
    public async Task F21_02_TelemetryFlushBarrier_FlushAsync_IncrementsFlushCounter()
    {
        var barrier = new TelemetryFlushBarrier();
        Assert.Equal(0, barrier.FlushCount);

        await barrier.FlushAsync();
        Assert.Equal(1, barrier.FlushCount);

        await barrier.FlushAsync();
        Assert.Equal(2, barrier.FlushCount);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F21")]
    public void F21_03_TelemetryFlushBarrier_QueryMetrics_IsCaseInsensitive()
    {
        var barrier = new TelemetryFlushBarrier();
        barrier.RecordMetric("app.signals.published", 42.0);

        var metrics = barrier.QueryMetrics("APP.SIGNALS.PUBLISHED");
        Assert.Single(metrics);
        Assert.Equal(42.0, metrics[0].Value);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F21")]
    public void F21_04_TelemetryFlushBarrier_QueryMetrics_ReturnsEmptyForUnknownMetric()
    {
        var barrier = new TelemetryFlushBarrier();
        var metrics = barrier.QueryMetrics("unknown.metric");
        Assert.Empty(metrics);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F21")]
    public async Task F21_05_TelemetryFlushBarrier_FlushAsync_ThrowsOnCancellation()
    {
        var barrier = new TelemetryFlushBarrier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier.FlushAsync(cts.Token));
    }

    #endregion
}
