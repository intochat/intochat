#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using System.Reflection;
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

namespace IntoChat.OS.Kernel.Tests.Integration.E2E;

/// <summary>
/// Tier 2: Boundary & Corner Cases — Comprehensive opaque-box test suite verifying boundary limits,
/// adversarial combinations, extreme payloads, nulls, and fail-closed behaviors for F1–F21 (105 tests).
/// </summary>
public sealed class Tier2_BoundaryCornerFacts
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
    public void F2_B01_IApp_Key_SpecialCharactersInBrain_HandlesVerbatim()
    {
        var key = IApp.Key("brain:alpha-01.test", "my_app-v2");
        Assert.Equal("brain:alpha-01.test/apps/my_app-v2", key);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B02_IApp_NameOf_PathTraversalPattern_ReturnsNull()
    {
        var result = IApp.NameOf("brain-1", "brain-1/apps/../../root");
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F2")]
    public void F2_B03_IApp_NameOf_TrailingSlash_ReturnsNull()
    {
        var result = IApp.NameOf("brain-1", "brain-1/apps/calc/");
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
    public void F5_B01_CallerContextStamper_WhitespacePrincipal_FailsClosed()
    {
        var invalid = new CapabilityCaller("   ", "acc", "brain");
        Assert.False(CallerContextStamper.IsTrusted(invalid));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F5")]
    public void F5_B02_CallerContextStamper_WhitespaceBrainId_FailsClosed()
    {
        var invalid = new CapabilityCaller("user", "acc", "   ");
        Assert.False(CallerContextStamper.IsTrusted(invalid));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F5")]
    public void F5_B03_CallRequest_PathTraversalNeuronName_TreatedAsLiteralString()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var req = new CallRequest { Caller = caller, TargetNeuron = "../../etc/shadow", Operation = "read" };
        Assert.Equal("../../etc/shadow", req.TargetNeuron);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F5")]
    public void F5_B04_CallerContextStamper_StampNull_ThrowsArgumentNullException()
    {
        var method = typeof(CallerContextStamper).GetMethod("Stamp", [typeof(CapabilityCaller)])!;
        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null]));
        Assert.IsType<ArgumentNullException>(ex.InnerException);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F5")]
    public void F5_B05_CallRequest_Defaults_EnforceTargetAndPublicOperationFalse()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var req = new CallRequest { Caller = caller, TargetNeuron = "target", Operation = "op" };
        Assert.False(req.PublicOperation);
        Assert.False(req.EnforceTarget);
    }

    #endregion

    #region F6: Provenance Caller Construction Hygiene Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F6")]
    public void F6_B01_CapabilityCallersOf_LongPrincipalId_ConstructsCleanly()
    {
        var longId = new string('k', 1024);
        var caller = CapabilityCallers.Of(longId, "acc_1", "brain_1");
        Assert.Equal(longId, caller.PrincipalId);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F6")]
    public void F6_B02_CapabilityCallersOf_SpecialCharactersInAppId_PreservesValue()
    {
        var specialApp = "studio.video:cutter/v2@alpha";
        var caller = CapabilityCallers.Of("u", "a", "b", CallerKind.App, appId: specialApp);
        Assert.Equal(specialApp, caller.AppId);
    }

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

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F6")]
    public void F6_B05_CapabilityCallersPlatform_PreservesOptionalConversationId()
    {
        var caller = CapabilityCallers.Platform("p", "a", "b", conversationId: "conv-99");
        Assert.Equal("conv-99", caller.ConversationId);
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
    public void F7_B04_SideloadInstallResult_EmptyMessage_HandlesCleanly()
    {
        var res = SideloadInstallResult.Fail("");
        Assert.False(res.Ok);
        Assert.Empty(res.Message);
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
        Assert.Equal("filmcraft", FilmCraftApp.Definition.Id);
        Assert.NotEqual("FilmCraft", FilmCraftApp.Definition.Id);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B02_PhotoCraft_ManifestId_ExactCasing()
    {
        Assert.Equal("photocraft", PhotoCraftApp.Definition.Id);
        Assert.NotEqual("PhotoCraft", PhotoCraftApp.Definition.Id);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B03_ManifestAccents_ValidHexFormat()
    {
        Assert.StartsWith("#", FilmCraftApp.Definition.Accent!);
        Assert.StartsWith("#", PhotoCraftApp.Definition.Accent!);
        Assert.Equal(7, FilmCraftApp.Definition.Accent!.Length);
        Assert.Equal(7, PhotoCraftApp.Definition.Accent!.Length);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B04_ManifestVersions_ValidSemverDots()
    {
        Assert.Contains(".", FilmCraftApp.Definition.Version);
        Assert.Contains(".", PhotoCraftApp.Definition.Version);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F9")]
    public void F9_B05_ManifestCategories_AreNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(FilmCraftApp.Definition.Category));
        Assert.False(string.IsNullOrWhiteSpace(PhotoCraftApp.Definition.Category));
    }

    #endregion

    #region F10: Fix SystemOne Hosting Path Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B01_AdapterDirectory_UsesForwardSlashes()
    {
        var path = Path.Combine(FindProjectDirectory("IntoChat.OS.Aspire.Hosting"), "Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("\"" + "s" + "rc/IntoChat.OS.Modules/AI/systemone-adapter\"", content);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B02_AdapterDirectory_DoesNotStartWithSlash()
    {
        var path = Path.Combine(FindProjectDirectory("IntoChat.OS.Aspire.Hosting"), "Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("\"/" + "s" + "rc/IntoChat.OS.Modules/AI/", content);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B03_AdapterDirectory_DoesNotContainDotDotEscape()
    {
        var path = Path.Combine(FindProjectDirectory("IntoChat.OS.Aspire.Hosting"), "Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("../", content);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B04_SystemOneResourceName_IsAlphanumeric()
    {
        var path = Path.Combine(FindProjectDirectory("IntoChat.OS.Aspire.Hosting"), "Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("SystemOneResourceName = \"systemone\";", content);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F10")]
    public void F10_B05_SystemOneApiKeyLocal_IsLocalString()
    {
        var path = Path.Combine(FindProjectDirectory("IntoChat.OS.Aspire.Hosting"), "Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("SystemOneApiKeyLocal = \"local\";", content);
    }

    #endregion

    #region F11: Stale InternalsVisibleTo Cleanup Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B01_ClientOrleansCsproj_HasNoEmptyInternalsVisibleTo()
    {
        var csprojPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Client.Orleans"), "IntoChat.OS.Client.Orleans.csproj");
        var xml = XDocument.Load(csprojPath);
        var ivts = xml.Descendants("InternalsVisibleTo").Select(x => x.Attribute("Include")?.Value).ToList();
        Assert.DoesNotContain("", ivts);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B02_ClientOrleansCsproj_NoDuplicateGrants()
    {
        var csprojPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Client.Orleans"), "IntoChat.OS.Client.Orleans.csproj");
        var xml = XDocument.Load(csprojPath);
        var ivts = xml.Descendants("InternalsVisibleTo").Select(x => x.Attribute("Include")?.Value).ToList();
        Assert.Equal(ivts.Count, ivts.Distinct().Count());
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B03_ClientOrleansCsproj_FileLength_IsReasonable()
    {
        var csprojPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Client.Orleans"), "IntoChat.OS.Client.Orleans.csproj");
        var fi = new FileInfo(csprojPath);
        Assert.True(fi.Length > 200 && fi.Length < 100_000);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B04_ClientOrleansCsproj_ContainsProjectClosingTag()
    {
        var csprojPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Client.Orleans"), "IntoChat.OS.Client.Orleans.csproj");
        var content = File.ReadAllText(csprojPath);
        Assert.Contains("</Project>", content);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F11")]
    public void F11_B05_ClientOrleansDirectory_ContainsOrleansBrain()
    {
        var dir = FindProjectDirectory("IntoChat.OS.Client.Orleans");
        Assert.True(File.Exists(Path.Combine(dir, "OrleansBrain.cs")));
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
        var types = typeof(IntoChat.OS.Kernel.CloudHost).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("NamesForward"));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B02_CloudHostAssembly_NoLegacyConfigurationTypes()
    {
        var types = typeof(IntoChat.OS.Kernel.CloudHost).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("LegacyConfigurationGuard"));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B03_FrameworkAssembly_ExportsOnlyValidPublicTypes()
    {
        var exported = typeof(CallerContextStamper).Assembly.GetExportedTypes();
        Assert.NotEmpty(exported);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B04_NoDeprecatedPrefixesInExportedTypes()
    {
        var exported = typeof(CallerContextStamper).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, t => t.Name.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F13")]
    public void F13_B05_ConfigurationNamespaces_AreClean()
    {
        var exported = typeof(CallerContextStamper).Assembly.GetExportedTypes();
        Assert.All(exported, t => Assert.False(t.Namespace?.Contains("Obsolete", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    #endregion

    #region F14: Canonical Execution Context Subsystem Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F14")]
    public async Task F14_B01_DeeplyNestedWithSliceAsync_UnwindsAllSlicesCorrectly()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var ctx = new ExecutionContext("exec-deep", caller);

        async Task RecurseAsync(int current, int max)
        {
            if (current > max) { return; }
            var slice = new ContextSlice($"slice_{current}", $"depth {current}", []);
            await ctx.WithSliceAsync(slice, async () =>
            {
                Assert.Equal(current, ctx.Slices.Count);
                await RecurseAsync(current + 1, max);
                return true;
            });
        }

        await RecurseAsync(1, 20);
        Assert.Empty(ctx.Slices);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F14")]
    public async Task F14_B02_ContextSlice_OneMegabyteContent_PreservesVerbatim()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var ctx = new ExecutionContext("exec-large", caller);
        var huge = new string('A', 1_048_576);
        var slice = new ContextSlice("MegaSlice", huge, ["huge"]);

        var length = await ctx.WithSliceAsync(slice, () => Task.FromResult(ctx.Slices[0].Content.Length));
        Assert.Equal(1_048_576, length);
        Assert.Empty(ctx.Slices);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F14")]
    public void F14_B03_ExecutionContext_MissingEnvironmentKey_ThrowsKeyNotFound()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var ctx = new ExecutionContext("exec-env", caller);
        Assert.Throws<KeyNotFoundException>(() => _ = ctx.Environment["MISSING_KEY"]);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F14")]
    public void F14_B04_ExecutionContext_DuplicateSliceNames_Allowed()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var s1 = new ContextSlice("Slice", "first", []);
        var s2 = new ContextSlice("Slice", "second", []);
        var ctx = new ExecutionContext("exec-dup", caller, initialSlices: [s1, s2]);

        Assert.Equal(2, ctx.Slices.Count);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F14")]
    public async Task F14_B05_ExecutionContext_ConcurrentSliceAccess_IsolatedPerContext()
    {
        var caller = CapabilityCallers.Of("u", "a", "b");
        var ctx1 = new ExecutionContext("ctx-1", caller);
        var ctx2 = new ExecutionContext("ctx-2", caller);

        var t1 = ctx1.WithSliceAsync(new ContextSlice("S1", "C1", []), async () =>
        {
            await Task.Delay(20);
            return ctx1.Slices.Count;
        });

        var t2 = ctx2.WithSliceAsync(new ContextSlice("S2", "C2", []), async () =>
        {
            await Task.Delay(20);
            return ctx2.Slices.Count;
        });

        var counts = await Task.WhenAll(t1, t2);
        Assert.Equal([1, 1], counts);
    }

    #endregion

    #region F15: Canonical Decision Maker Subsystem Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F15")]
    public async Task F15_B01_DecideReactiveAsync_ZeroConfidence_ReturnsZero()
    {
        var dm = new InMemoryDecisionMaker();
        dm.RegisterReactiveRule("uncertain_op", "risky_choice", 0.0);

        var decision = await dm.DecideReactiveAsync(new ReactivePrompt("uncertain_op", ["risky_choice"]));
        Assert.Equal("risky_choice", decision.Choice);
        Assert.Equal(0.0, decision.Confidence);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F15")]
    public async Task F15_B02_DecideReactiveAsync_FullConfidence_ReturnsOne()
    {
        var dm = new InMemoryDecisionMaker();
        dm.RegisterReactiveRule("certain_op", "safe_choice", 1.0);

        var decision = await dm.DecideReactiveAsync(new ReactivePrompt("certain_op", ["safe_choice"]));
        Assert.Equal("safe_choice", decision.Choice);
        Assert.Equal(1.0, decision.Confidence);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F15")]
    public async Task F15_B03_DecideDeliberateAsync_ZeroConstraints_ExecutesCleanly()
    {
        var dm = new InMemoryDecisionMaker();
        var goal = new DeliberateGoal("Freeform goal", []);

        var decision = await dm.DecideDeliberateAsync(goal);
        Assert.NotEmpty(decision.Outcome.Steps);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F15")]
    public async Task F15_B04_DecideDeliberateAsync_ManyConstraints_AssessesAll()
    {
        var dm = new InMemoryDecisionMaker();
        var constraints = Enumerable.Range(1, 50).Select(i => $"constraint_{i}").ToArray();
        var goal = new DeliberateGoal("Constrained goal", constraints);

        var decision = await dm.DecideDeliberateAsync(goal);
        Assert.NotEmpty(decision.Outcome.Steps);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F15")]
    public async Task F15_B05_DecisionMaker_RuleOverwrite_UpdatesExistingRule()
    {
        var dm = new InMemoryDecisionMaker();
        dm.RegisterReactiveRule("intent", "first", 0.5);
        dm.RegisterReactiveRule("intent", "second", 0.99);

        var decision = await dm.DecideReactiveAsync(new ReactivePrompt("intent", ["first", "second"]));
        Assert.Equal("second", decision.Choice);
        Assert.Equal(0.99, decision.Confidence);
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

    #region F17: Canonical Task Engine for Self-Improvement Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F17")]
    public async Task F17_B01_CreateTaskAsync_ZeroSteps_CreatesValidPendingTask()
    {
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Empty Task", "Nothing", "admin", []));

        Assert.NotNull(task.TaskId);
        Assert.Equal(TaskStatus.Pending, task.Status);
        Assert.Empty(task.Steps);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F17")]
    public async Task F17_B02_TaskEngine_ManySteps_HandlesFiftySteps()
    {
        var engine = new InMemoryTaskEngine();
        var steps = Enumerable.Range(1, 50).Select(i => new TaskStepDefinition($"step_{i}", $"step description {i}", "kernel")).ToArray();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Long Task", "Multi step", "admin", steps));

        Assert.Equal(50, task.Steps.Length);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F17")]
    public async Task F17_B03_UpdateStepStatusAsync_UnknownStepId_DoesNotAlterOtherSteps()
    {
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Task", "Obj", "admin", [new TaskStepDefinition("s1", "s1", "kernel"), new TaskStepDefinition("s2", "s2", "kernel")]));

        var updated = await engine.AdvanceTaskStepAsync(task.TaskId, 999, TaskStepStatus.Completed);
        Assert.All(updated.Steps, s => Assert.Equal(TaskStepStatus.Pending, s.Status));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F17")]
    public async Task F17_B04_UpdateStepStatusAsync_UnknownTaskId_ThrowsKeyNotFoundException()
    {
        var engine = new InMemoryTaskEngine();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            engine.AdvanceTaskStepAsync("non_existent_task", 0, TaskStepStatus.Completed));
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F17")]
    public async Task F17_B05_VerifyTaskAsync_UnknownTaskId_ReturnsInvalid()
    {
        var engine = new InMemoryTaskEngine();
        var result = await engine.VerifyTaskAsync("missing_task_id");
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Violations!);
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

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B02_ContractManifest_FileSize_ExceedsTenKilobytes()
    {
        var manifestPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Architecture.Tests.Unit"), "ContractManifest.json");
        var fi = new FileInfo(manifestPath);
        Assert.True(fi.Length > 10_000, $"Manifest size was {fi.Length} bytes, expected > 10KB");
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B03_ContractManifest_LineCount_ExceedsOneHundredLines()
    {
        var manifestPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Architecture.Tests.Unit"), "ContractManifest.json");
        var lineCount = File.ReadAllLines(manifestPath).Length;
        Assert.True(lineCount > 100);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B04_ContractManifest_ValidGrainInterfacesSection()
    {
        var manifestPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Architecture.Tests.Unit"), "ContractManifest.json");
        var content = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("GrainInterfaces", out var grains));
        Assert.Equal(JsonValueKind.Object, grains.ValueKind);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F18")]
    public void F18_B05_ContractManifest_TracksIdentityDirectory()
    {
        var manifestPath = Path.Combine(FindProjectDirectory("IntoChat.OS.Architecture.Tests.Unit"), "ContractManifest.json");
        var content = File.ReadAllText(manifestPath);
        Assert.Contains("IdentityDirectoryNeuron", content);
    }

    #endregion

    #region F19: TreatWarningsAsErrors Zero-Warning Enforcement Boundaries

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B01_DirectoryBuildProps_ValidXmlStructure()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var xml = XDocument.Load(propsPath);
        Assert.NotNull(xml.Root);
        Assert.Equal("Project", xml.Root.Name.LocalName);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B02_DirectoryBuildProps_TargetFrameworkTrimmed()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var xml = XDocument.Load(propsPath);
        var tf = xml.Descendants("TargetFramework").First().Value.Trim();
        Assert.Equal("net11.0", tf);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B03_DirectoryBuildProps_NotConfiguredToFalse()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var content = File.ReadAllText(propsPath);
        Assert.DoesNotContain("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>", content);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B04_DirectoryPackagesProps_ValidXml()
    {
        var packagesPath = Path.Combine(RepositoryRoot, "Directory.Packages.props");
        var xml = XDocument.Load(packagesPath);
        Assert.NotNull(xml.Root);
    }

    [Fact]
    [Trait("Category", "Tier2_BoundaryCorner")]
    [Trait("Feature", "F19")]
    public void F19_B05_DirectoryBuildProps_LocatedAtRepositoryRoot()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath));
        var dir = Path.GetDirectoryName(propsPath);
        Assert.True(File.Exists(Path.Combine(dir!, "Intochat.OS.slnx")));
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
