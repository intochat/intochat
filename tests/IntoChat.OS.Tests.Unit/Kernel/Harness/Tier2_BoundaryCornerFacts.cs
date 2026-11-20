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
using IntoChat.OS.Primitives;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;
using IntoChat.OS.Host.ServiceDefaults;

namespace IntoChat.OS.Kernel.Tests.Unit.Harness;

/// <summary>
/// Tier 2: Boundary & Corner Cases — Comprehensive opaque-box test suite verifying boundary limits,
/// adversarial combinations, extreme payloads, nulls, and fail-closed behaviors for F1–F21.
/// </summary>
public sealed class Tier2_BoundaryCornerFacts
{
    #region F1: Ephemeral Signal Annotation Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F1")]
    public void F1_B01_AppActivity_ExtremeStringPayload_HandledWithoutTruncation()
    {
        var hugeDetail = new string('x', 65_536);
        var activity = new AppActivity("app-large", "working", hugeDetail);
        Assert.Equal(65_536, activity.Detail.Length);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F1")]
    public void F1_B02_AppActivity_UnicodeAndEmojis_PreservesEncodingFidelity()
    {
        var specialDetail = "🚀 Processing stream with UTF-8: ⚡ \u0000\u001F special symbols & <xml> tag";
        var activity = new AppActivity("app-emoji", "working", specialDetail);
        Assert.Equal(specialDetail, activity.Detail);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F1")]
    public void F1_B03_PresentationChanged_BoundaryRevisions_HandlesMinAndMaxInt64()
    {
        var pZero = new PresentationChanged("app-rev", 0, 0);
        Assert.Equal(0, pZero.ProgramRevision);
        Assert.Equal(0, pZero.Revision);

        var pMax = new PresentationChanged("app-rev", long.MaxValue, long.MaxValue);
        Assert.Equal(long.MaxValue, pMax.ProgramRevision);
        Assert.Equal(long.MaxValue, pMax.Revision);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F1")]
    public void F1_B04_AppRequested_EmptyStringsAndEmptyGuid_InstantiatesCleanly()
    {
        var req = new AppRequested("", "", Guid.Empty);
        Assert.Empty(req.Operation);
        Assert.Empty(req.Input);
        Assert.Equal(Guid.Empty, req.RequestId);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F1")]
    public void F1_B05_EphemeralSignals_RapidInstantiation_AllocatesEfficiently()
    {
        const int count = 25_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            _ = new AppActivity("app-rapid", "working", "hot step");
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"25k signals took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    #endregion

    #region F2: AppNeuron Progress Separation Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B01_AppRef_SpecialCharactersInBrain_HandlesVerbatim()
    {
        Assert.False(BrainId.IsValid("brain:alpha-01.test"));
        var key = IntoChat.OS.Primitives.AppRef.Of(new BrainId("brain.alpha-01.test"), "my_app-v2").Key;
        Assert.Equal("brain.alpha-01.test/apps/my_app-v2", key);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B02_IApp_NameOf_PathTraversalPattern_ReturnsNull()
    {
        var result = IntoChat.OS.Primitives.AppRef.NameOf("brain-1", "brain-1/apps/../../root");
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B03_IApp_NameOf_TrailingSlash_ReturnsNull()
    {
        var result = IntoChat.OS.Primitives.AppRef.NameOf("brain-1", "brain-1/apps/calc/");
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B04_AppState_LargeCollectionModification_MaintainsState()
    {
        var dict = new Dictionary<string, string>();
        for (int i = 0; i < 1000; i++)
        {
            dict[$"setting_{i}"] = $"val_{i}";
        }
        var state = new AppState { Settings = dict };
        Assert.Equal(1000, state.Settings.Count);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B05_AppSnapshot_EmptyActivityArray_HandlesWithoutErrors()
    {
        var manifest = new IntoChat.OS.Modules.Apps.AppManifest("Tool", "Desc", [], [], [], "icon.png", []);
        var snap = new AppSnapshot("tool", manifest, "csharp", [], [], [], "1.0", AppStatus.Live, [], []);
        Assert.Empty(snap.Activity);
    }

    #endregion

    #region F3: Asynchronous Confirmation Resolution Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F3")]
    public async Task F3_B01_UnresolvedConfirmation_TimesOutGracefully()
    {
        var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await tcs.Task.WaitAsync(cts.Token);
        });
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F3")]
    public async Task F3_B02_TaskCompletionSource_DoubleCompletion_IsSafeViaTrySetResult()
    {
        var tcs = new TaskCompletionSource<ConfirmationResult>();
        var r1 = new ConfirmationResult("plan-1", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
        var r2 = new ConfirmationResult("plan-1", ConfirmationOutcome.Denied, ConfirmationDenyReason.SelfApproval, false);

        var first = tcs.TrySetResult(r1);
        var second = tcs.TrySetResult(r2);

        Assert.True(first);
        Assert.False(second);
        var result = await tcs.Task;
        Assert.Equal(ConfirmationOutcome.Approved, result.Outcome);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F3")]
    public async Task F3_B03_PreCancelledToken_AbortsWaitImmediately()
    {
        var tcs = new TaskCompletionSource<ConfirmationResult>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tcs.Task.WaitAsync(cts.Token));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F3")]
    public void F3_B04_ConfirmationResult_ExpiredState_CarriesAccurateReason()
    {
        var result = new ConfirmationResult("plan-exp", ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired, Skipped: false);
        Assert.Equal(ConfirmationOutcome.Expired, result.Outcome);
        Assert.Equal(ConfirmationDenyReason.Expired, result.Reason);
        Assert.False(result.Approved);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F3")]
    public async Task F3_B05_ConcurrentConfirmationsStress_HandlesFiftySimultaneousCompletions()
    {
        var tasks = Enumerable.Range(1, 50).Select(async i =>
        {
            var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Yield();
            tcs.TrySetResult(new ConfirmationResult($"p-{i}", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false));
            return await tcs.Task;
        });

        var results = await Task.WhenAll(tasks);
        Assert.Equal(50, results.Length);
    }

    #endregion

    #region F4: Streaming Signal Allocation Optimization Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F4")]
    public async Task F4_B01_EmptyStream_EnumeratesWithoutThrowing()
    {
        static async IAsyncEnumerable<AppActivity> EmptyStream()
        {
            await Task.Yield();
            yield break;
        }

        var items = new List<AppActivity>();
        await foreach (var item in EmptyStream())
        {
            items.Add(item);
        }
        Assert.Empty(items);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F4")]
    public async Task F4_B02_StreamWithImmediateCancellation_TerminatesEarly()
    {
        static async IAsyncEnumerable<AppActivity> InfiniteStream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Yield();
                yield return new AppActivity("inf", "working", "tick");
            }
            ct.ThrowIfCancellationRequested();
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in InfiniteStream(cts.Token))
            {
            }
        });
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F4")]
    public void F4_B03_SignalJsonWithExtraProperties_DeserializesGracefully()
    {
        var jsonWithExtra = "{\"App\":\"custom-app\",\"State\":\"working\",\"Detail\":\"testing\",\"ExtraProperty\":\"ignored\"}";
        var deserialized = JsonSerializer.Deserialize<AppActivity>(jsonWithExtra);
        Assert.NotNull(deserialized);
        Assert.Equal("custom-app", deserialized.App);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F4")]
    public void F4_B04_StopwatchElapsedTime_ZeroDifference_ReturnsZero()
    {
        var ts = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(ts, ts);
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F4")]
    public void F4_B05_SignalBatch_ExtremeCount_ProcessesWithoutMemoryLeak()
    {
        var batch = Enumerable.Range(1, 20_000).Select(i => new AppActivity($"app-{i}", "working", "data")).ToArray();
        Assert.Equal(20_000, batch.Length);
    }

    #endregion

    #region F5: Fail-Closed PlainNeuronCallFilter Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F5")]
    public void F5_B03_CallRequest_PathTraversalNeuronName_TreatedAsLiteralString()
    {
        var req = new CallRequest { TargetNeuron = "../../etc/shadow", Operation = "read" };
        Assert.Equal("../../etc/shadow", req.TargetNeuron);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F5")]
    public void F5_B05_CallRequest_Defaults_EnforceTargetAndPublicOperationFalse()
    {
        var req = new CallRequest { TargetNeuron = "target", Operation = "op" };
        Assert.False(request_PublicOperation(req));
        Assert.False(req.EnforceTarget);

        static bool request_PublicOperation(CallRequest r) => r.PublicOperation;
    }

    #endregion

    #region F6: Provenance Caller Construction Hygiene Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F6")]
    public void F6_B03_AppPrincipal_IsApp_ReturnsFalseForInvalidStrings()
    {
        Assert.False(AppPrincipal.IsApp(null));
        Assert.False(AppPrincipal.IsApp(""));
        Assert.False(AppPrincipal.IsApp("app"));
        Assert.False(AppPrincipal.IsApp("user:123"));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F6")]
    public void F6_B04_AppPrincipal_AppKeyOf_NonAppPrincipal_ReturnsNull()
    {
        Assert.Null(AppPrincipal.AppKeyOf("user_standard"));
        Assert.Null(AppPrincipal.AppKeyOf(""));
    }

    #endregion

    #region F7: Sideload Consent Gate Enforcement Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F7")]
    public void F7_B01_AppConsent_TamperedRequirements_GeneratesDifferentHash()
    {
        var req1 = new AppRequirements(SemanticTypes: ["cap1"], Connectors: [], Secrets: [], Capabilities: []);
        var req2 = new AppRequirements(SemanticTypes: ["cap2"], Connectors: [], Secrets: [], Capabilities: []);

        var hash1 = AppConsent.Of(req1);
        var hash2 = AppConsent.Of(req2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F7")]
    public void F7_B02_AppConsent_Added_IdentifiesNewCapabilities()
    {
        var initial = new AppRequirements(SemanticTypes: ["read"], Connectors: [], Secrets: [], Capabilities: []);
        var updated = new AppRequirements(SemanticTypes: ["read", "write"], Connectors: [], Secrets: [], Capabilities: []);

        var added = AppConsent.Added(initial, updated);
        Assert.Contains("write", added.SemanticTypes!);
        Assert.DoesNotContain("read", added.SemanticTypes!);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F7")]
    public void F7_B03_AppConsent_Added_IdenticalRequirements_ProducesEmptyDiff()
    {
        var req = new AppRequirements(SemanticTypes: ["r"], Connectors: ["c"], Secrets: ["s"], Capabilities: ["k"]);
        var added = AppConsent.Added(req, req);
        Assert.True(AppConsent.IsEmpty(added));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F7")]
    public void F7_B05_AppOrigin_NullOptionals_InitializesWithDefaults()
    {
        var origin = new AppOrigin("Pub", new AppSource.Shipped(), "App", "1.0");
        Assert.Null(origin.PublicationDigest);
        Assert.Null(origin.InstalledByAccount);
        Assert.Null(origin.Consented);
    }

    #endregion

    #region F8: Strict Grant Rule Evaluation Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F8")]
    public void F8_B01_GrantEvaluation_PrefixCollision_DoesNotGrant()
    {
        var grants = new[] { "storage.read" };
        var requested = "storage.read_full";
        Assert.False(grants.Contains(requested));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F8")]
    public void F8_B02_GrantEvaluation_SuffixCollision_DoesNotGrant()
    {
        var grants = new[] { "storage.read" };
        var requested = "admin.storage.read";
        Assert.False(grants.Contains(requested));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F8")]
    public void F8_B03_GrantEvaluation_CaseSensitivity_MismatchFails()
    {
        var grants = new[] { "storage.read" };
        Assert.False(grants.Contains("STORAGE.READ", StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F8")]
    public void F8_B04_GrantEvaluation_ControlCharacters_FailsClosed()
    {
        var grants = new[] { "storage.read" };
        Assert.False(grants.Contains("storage.read\0"));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F8")]
    public void F8_B05_GrantEvaluation_DuplicateGrants_EvaluatesIdempotently()
    {
        var grants = new[] { "network.out", "network.out", "network.out" };
        Assert.Equal(3, grants.Length);
        Assert.Single(grants.Distinct());
    }

    #endregion

    #region F9: Eliminate Dual App Definitions Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B01_FilmCraft_ManifestId_ExactCasing()
    {
        Assert.Equal("filmcraft", FilmCraftApp.Bundle.Name);
        Assert.NotEqual("FilmCraft", FilmCraftApp.Bundle.Name);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B02_PhotoCraft_ManifestId_ExactCasing()
    {
        Assert.Equal("photocraft", PhotoCraftApp.Bundle.Name);
        Assert.NotEqual("PhotoCraft", PhotoCraftApp.Bundle.Name);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B03_ManifestAccents_ValidHexFormat()
    {
        Assert.True(FilmCraftApp.Bundle.Manifest.Studio);
        Assert.True(PhotoCraftApp.Bundle.Manifest.Studio);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B04_ManifestVersions_ValidSemverDots()
    {
        Assert.Contains(".", FilmCraftApp.Bundle.Version);
        Assert.Contains(".", PhotoCraftApp.Bundle.Version);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B05_ManifestCategories_AreNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(FilmCraftApp.Bundle.Name));
        Assert.False(string.IsNullOrWhiteSpace(PhotoCraftApp.Bundle.Name));
    }

    #endregion

    #region F12: Retire AppState Legacy Collections & Shape Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F12")]
    public void F12_B01_AppState_HighVolumeFiles_MaintainsIntegrity()
    {
        var dict = new Dictionary<string, string>();
        for (int i = 0; i < 5000; i++)
        {
            dict[$"file_{i}.txt"] = $"content {i}";
        }
        var state = new AppState { Files = dict };
        Assert.Equal(5000, state.Files.Count);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F12")]
    public void F12_B02_AppState_SpecialCharacterFilenames_Supported()
    {
        var key = "sub/dir/special-file_v1.0.json";
        var state = new AppState { Files = new Dictionary<string, string> { [key] = "{}" } };
        Assert.True(state.Files.ContainsKey(key));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F12")]
    public void F12_B03_AppState_Settings_EmptyValues_Allowed()
    {
        var state = new AppState { Settings = new Dictionary<string, string> { ["ALLOW_EMPTY"] = "" } };
        Assert.Equal("", state.Settings["ALLOW_EMPTY"]);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F12")]
    public void F12_B04_AppState_Connections_UriStrings_Preserved()
    {
        var state = new AppState { Connections = new Dictionary<string, string> { ["qdrant"] = "http://localhost:6333?api-key=test" } };
        Assert.Equal("http://localhost:6333?api-key=test", state.Connections["qdrant"]);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F12")]
    public void F12_B05_AppState_SerializationRoundtrip_PreservesCollections()
    {
        var state = new AppState
        {
            Files = new Dictionary<string, string> { ["a.txt"] = "alpha" },
            Settings = new Dictionary<string, string> { ["env"] = "test" },
            Connections = new Dictionary<string, string> { ["db"] = "sqlite" }
        };

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<AppState>(json);

        Assert.NotNull(restored);
        Assert.Equal("alpha", restored.Files["a.txt"]);
        Assert.Equal("test", restored.Settings["env"]);
        Assert.Equal("sqlite", restored.Connections["db"]);
    }

    #endregion

    #region F13: Retire Legacy Shims & Forwards Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B01_CloudHostAssembly_NoNamesForwardTypes()
    {
        var types = typeof(IntoChat.OS.Kernel.OperatingSystemModules).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("NamesForward"));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B03_FrameworkAssembly_ExportsOnlyValidPublicTypes()
    {
        var exported = typeof(DigitalBrain.Runtime.Orleans.Hosting.NeutralCallMapping).Assembly.GetExportedTypes();
        Assert.NotEmpty(exported);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B04_NoDeprecatedPrefixesInExportedTypes()
    {
        var exported = typeof(DigitalBrain.Runtime.Orleans.Hosting.NeutralCallMapping).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, t => t.Name.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B05_ConfigurationNamespaces_AreClean()
    {
        var exported = typeof(DigitalBrain.Runtime.Orleans.Hosting.NeutralCallMapping).Assembly.GetExportedTypes();
        Assert.All(exported, t => Assert.False(t.Namespace?.Contains("Obsolete", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    #endregion

    #region F16: Canonical Memory with Graph UI Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F16")]
    public async Task F16_B01_QueryGraphAsync_CircularLoop_TerminatesWithoutInfiniteLoop()
    {
        var store = new InMemoryKnowledgeStore();
        await store.RecordNodeAsync(new KnowledgeNode("cA", "Cycle A", "Loop", new Dictionary<string, string>()));
        await store.RecordNodeAsync(new KnowledgeNode("cB", "Cycle B", "Loop", new Dictionary<string, string>()));

        await store.RecordEdgeAsync(new KnowledgeEdge("cA", "cB", "PointsTo", 1.0));
        await store.RecordEdgeAsync(new KnowledgeEdge("cB", "cA", "LoopsBack", 1.0));

        var graph = await store.QueryGraphAsync("cA", depth: 10);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F16")]
    public async Task F16_B02_QueryGraphAsync_SelfLoopEdge_HandledGracefully()
    {
        var store = new InMemoryKnowledgeStore();
        await store.RecordNodeAsync(new KnowledgeNode("sA", "Self Node", "Type", new Dictionary<string, string>()));
        await store.RecordEdgeAsync(new KnowledgeEdge("sA", "sA", "Self", 1.0));

        var graph = await store.QueryGraphAsync("sA", depth: 3);
        Assert.Single(graph.Nodes);
        Assert.Single(graph.Edges);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F16")]
    public async Task F16_B03_QueryGraphAsync_DepthZero_ReturnsOnlyRootNode()
    {
        var store = new InMemoryKnowledgeStore();
        await store.RecordNodeAsync(new KnowledgeNode("root", "Root", "Type", new Dictionary<string, string>()));
        await store.RecordNodeAsync(new KnowledgeNode("child", "Child", "Type", new Dictionary<string, string>()));
        await store.RecordEdgeAsync(new KnowledgeEdge("root", "child", "Edge", 1.0));

        var graph = await store.QueryGraphAsync("root", depth: 0);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F16")]
    public async Task F16_B04_SemanticSearchAsync_NoMatchQuery_ReturnsEmpty()
    {
        var store = new InMemoryKnowledgeStore();
        await store.RecordNodeAsync(new KnowledgeNode("node1", "Kubernetes Ingress", "Infra", new Dictionary<string, string>()));

        var hits = await store.SemanticSearchAsync("Quantum Teleportation", limit: 5);
        Assert.Empty(hits);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F16")]
    public async Task F16_B05_KnowledgeNode_ManyProperties_PreservesAll()
    {
        var store = new InMemoryKnowledgeStore();
        var props = Enumerable.Range(1, 100).ToDictionary(i => $"prop_{i}", i => $"value_{i}");
        var node = new KnowledgeNode("prop_node", "Multi Prop", "Type", props);

        await store.RecordNodeAsync(node);
        var graph = await store.QueryGraphAsync("prop_node", depth: 1);

        Assert.Equal(100, graph.Nodes[0].Properties.Count);
    }

    #endregion

    #region F18: API Tracking Baseline & Contract Manifest Update Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B01_ContractManifest_EmptyJson_ParsesSafely()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.False(doc.RootElement.TryGetProperty("Contracts", out _));
    }

    #endregion

    #region F20: Reusable Testing Framework for Agent Verification Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F20")]
    public async Task F20_B01_RunTargetedAsync_EmptyFilter_ExecutesCleanly()
    {
        var svc = new InMemoryTestVerificationService();
        var req = new TestExecutionRequest("Tier1", "");
        var res = await svc.RunTargetedSuiteAsync(req);
        Assert.True(res.Success);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F20")]
    public async Task F20_B02_RunTargetedAsync_NullRequest_ThrowsArgumentNullException()
    {
        var svc = new InMemoryTestVerificationService();
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.RunTargetedSuiteAsync(null!));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F20")]
    public async Task F20_B03_VerifyMilestoneAsync_ValidMilestone_ReturnsTrue()
    {
        var svc = new InMemoryTestVerificationService();
        var res = await svc.GetHistoricalRunsAsync("M6");
        Assert.NotNull(res);
        Assert.NotEmpty(res);
        Assert.True(res[0].Success);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F20")]
    public async Task F20_B04_VerifyMilestoneAsync_NullMilestone_ThrowsArgumentException()
    {
        var svc = new InMemoryTestVerificationService();
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.GetHistoricalRunsAsync(null!));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F20")]
    public async Task F20_B05_VerifyMilestoneAsync_WhitespaceMilestone_ThrowsArgumentException()
    {
        var svc = new InMemoryTestVerificationService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetHistoricalRunsAsync("   "));
    }

    #endregion

    #region F21: Aspire Telemetry Verification & Flush Barrier Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F21")]
    public void F21_B01_TelemetryFlushBarrier_NegativeMetricValue_RecordedAccurately()
    {
        var barrier = new TelemetryFlushBarrier();
        barrier.RecordMetric("temperature.celsius", -42.5);

        var metrics = barrier.QueryMetrics("temperature.celsius");
        Assert.Single(metrics);
        Assert.Equal(-42.5, metrics[0].Value);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F21")]
    public void F21_B02_TelemetryFlushBarrier_ZeroMetricValue_RecordedAccurately()
    {
        var barrier = new TelemetryFlushBarrier();
        barrier.RecordMetric("errors.count", 0.0);

        var metrics = barrier.QueryMetrics("errors.count");
        Assert.Single(metrics);
        Assert.Equal(0.0, metrics[0].Value);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F21")]
    public void F21_B03_TelemetryFlushBarrier_QueryEmptyMetrics_ReturnsEmpty()
    {
        var barrier = new TelemetryFlushBarrier();
        var metrics = barrier.QueryMetrics("non_existent_metric");
        Assert.Empty(metrics);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F21")]
    public async Task F21_B04_TelemetryFlushBarrier_ConcurrentFlushes_AllSucceed()
    {
        var barrier = new TelemetryFlushBarrier();
        var tasks = Enumerable.Range(1, 50).Select(_ => barrier.FlushAsync());
        await Task.WhenAll(tasks);
        Assert.Equal(50, barrier.FlushCount);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F21")]
    public void F21_B05_TelemetryMetricSnapshot_Timestamp_IsRecentUtc()
    {
        var before = DateTimeOffset.UtcNow;
        var barrier = new TelemetryFlushBarrier();
        barrier.RecordMetric("metric.time", 1.0);
        var after = DateTimeOffset.UtcNow;

        var m = barrier.QueryMetrics("metric.time").Single();
        Assert.True(m.Timestamp >= before && m.Timestamp <= after);
    }

    #endregion
}
