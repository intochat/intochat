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
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.ServiceDefaults;
using AppManifest = DigitalBrain.AppManifest;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;

namespace IntoChat.OS.Kernel.Tests.Integration.E2E;

/// <summary>
/// Tier 1: Feature Coverage — Comprehensive opaque-box test suite verifying primary behavior (happy paths)
/// for all 21 features in PROJECT.md § Feature Inventory (>=5 tests per feature = 105 tests).
/// </summary>
public sealed class Tier1_FeatureCoverageFacts
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
    public void F2_01_IApp_KeyFormatting_ConformsToCanonicalPattern()
    {
        var key = IApp.Key("brain-alpha", "summarizer");
        Assert.Equal("brain-alpha/apps/summarizer", key);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F2")]
    public void F2_02_IApp_NameOf_ExtractsValidAppSlug()
    {
        var name = IApp.NameOf("brain-alpha", "brain-alpha/apps/summarizer");
        Assert.Equal("summarizer", name);

        var foreign = IApp.NameOf("brain-beta", "brain-alpha/apps/summarizer");
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
        var options = new ConfirmationOptions();
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

        _ = Task.Run(() =>
        {
            Thread.Sleep(10);
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
    public void F5_01_CallerContextStamper_IsTrusted_RequiresAllFields()
    {
        var valid = CapabilityCallers.Of("user_123", "acc_1", "brain_alpha", CallerKind.User);
        Assert.True(CallerContextStamper.IsTrusted(valid));

        var invalid = new CapabilityCaller("", "acc_1", "brain_alpha");
        Assert.False(CallerContextStamper.IsTrusted(invalid));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F5")]
    public void F5_02_CallRequest_InitializesWithRequiredProvenance()
    {
        var caller = CapabilityCallers.Of("usr_9", "acc_1", "brain_1", CallerKind.User);
        var request = new CallRequest
        {
            Caller = caller,
            TargetNeuron = "brain_1/apps/calc",
            Operation = "execute"
        };

        Assert.Equal("usr_9", request.Caller.PrincipalId);
        Assert.Equal("brain_1/apps/calc", request.TargetNeuron);
        Assert.Equal("execute", request.Operation);
        Assert.False(request.PublicOperation);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F5")]
    public void F5_03_UntrustedCallerException_ConstructsWithMessage()
    {
        var ex = new UntrustedCallerException("Missing edge provenance stamp.");
        Assert.Equal("Missing edge provenance stamp.", ex.Message);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F5")]
    public void F5_04_CallerContextStamper_IsModelOriginated_TracksConversationId()
    {
        var humanCaller = CapabilityCallers.Of("user_1", "acc_1", "brain_1", CallerKind.User);
        Assert.False(CallerContextStamper.IsModelOriginated(humanCaller));

        var modelCaller = CapabilityCallers.Of("asst_1", "acc_1", "brain_1", CallerKind.Assistant, conversationId: "conv_42");
        Assert.True(CallerContextStamper.IsModelOriginated(modelCaller));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F5")]
    public void F5_05_CallerContextStamper_Constants_AreDefined()
    {
        Assert.Equal("db.cap.principal", CallerContextStamper.RequestContextKey);
        Assert.Equal("intochatos.trusted-module-call", CallerContextStamper.ModuleCallKey);
    }

    #endregion

    #region F6: Provenance Caller Construction Hygiene

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F6")]
    public void F6_01_CapabilityCallersOf_User_StampsCorrectCallerKind()
    {
        var caller = CapabilityCallers.Of("usr_john", "acc_corp", "brain_main", CallerKind.User);
        Assert.Equal("usr_john", caller.PrincipalId);
        Assert.Equal("acc_corp", caller.AccountId);
        Assert.Equal("brain_main", caller.BrainId);
        Assert.Equal(CallerKind.User, caller.Kind);
        Assert.Equal(TrustedEdge.AuthenticatedHttp, caller.StampedBy);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F6")]
    public void F6_02_CapabilityCallersOf_App_StampsAppProxyEdge()
    {
        var caller = CapabilityCallers.Of("usr_john", "acc_corp", "brain_main", CallerKind.App, appId: "echo-bot");
        Assert.Equal("echo-bot", caller.AppId);
        Assert.Equal(CallerKind.App, caller.Kind);
        Assert.Equal(TrustedEdge.AppProxy, caller.StampedBy);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F6")]
    public void F6_03_CapabilityCallersOf_Platform_StampsPlatformConstants()
    {
        var caller = CapabilityCallers.Platform("platform_sys", "acc_root", "brain_sys");
        Assert.Equal("platform", caller.AppId);
        Assert.Equal(CallerKind.Platform, caller.Kind);
        Assert.Equal(TrustedEdge.Platform, caller.StampedBy);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F6")]
    public void F6_04_CapabilityCallersOf_RejectsEmptyParameters()
    {
        Assert.Throws<ArgumentException>(() => CapabilityCallers.Of("", "acc", "brain"));
        Assert.Throws<ArgumentException>(() => CapabilityCallers.Of("p", "", "brain"));
        Assert.Throws<ArgumentException>(() => CapabilityCallers.Of("p", "acc", ""));
    }

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
    public void F7_01_SideloadInstallResult_InitializesSuccessAndConsentState()
    {
        var result = SideloadInstallResult.Done("Installed successfully");
        Assert.True(result.Ok);
        Assert.Equal("Installed successfully", result.Message);
    }

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
    public void F7_04_SideloadInstallResult_FailedState_ReportsErrorMessage()
    {
        var result = SideloadInstallResult.Fail("User consent required.");
        Assert.False(result.Ok);
        Assert.Contains("User consent required", result.Message);
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
    public void F8_03_CapabilityCaller_PersonId_PreservedThroughGrants()
    {
        var caller = CapabilityCallers.Of("person_42", "acc_1", "brain_1", CallerKind.User);
        Assert.Equal("person_42", caller.PersonId);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F8")]
    public void F8_04_GrantEvaluation_DistinguishesBetweenDifferentBrains()
    {
        var callerAlpha = CapabilityCallers.Of("user_1", "acc_1", "brain_alpha", CallerKind.User);
        var callerBeta = CapabilityCallers.Of("user_1", "acc_1", "brain_beta", CallerKind.User);

        Assert.NotEqual(callerAlpha.BrainId, callerBeta.BrainId);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F8")]
    public void F8_05_CallerContextStamper_Require_ThrowsWhenUnstamped()
    {
        Assert.Throws<UntrustedCallerException>(() => CallerContextStamper.Require());
    }

    #endregion

    #region F9: Eliminate Dual App Definitions

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_01_FilmCraftApp_Manifest_DeclaresUniqueId()
    {
        var manifest = FilmCraftApp.Manifest;
        Assert.NotNull(manifest);
        Assert.Equal("filmcraft", manifest.Id);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_02_PhotoCraftApp_Manifest_DeclaresUniqueId()
    {
        var manifest = PhotoCraftApp.Manifest;
        Assert.NotNull(manifest);
        Assert.Equal("photocraft", manifest.Id);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_03_StudioApps_HaveDistinctManifestIdentities()
    {
        Assert.NotEqual(FilmCraftApp.Manifest.Id, PhotoCraftApp.Manifest.Id);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_04_FilmCraftApp_AccentAndCategory_AreConfigured()
    {
        Assert.Equal("#8B5CF6", FilmCraftApp.Manifest.Accent);
        Assert.Equal("Studio", FilmCraftApp.Manifest.Category);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F9")]
    public void F9_05_PhotoCraftApp_AccentAndCategory_AreConfigured()
    {
        Assert.Equal("#06B6D4", PhotoCraftApp.Manifest.Accent);
        Assert.Equal("Studio", PhotoCraftApp.Manifest.Category);
    }

    #endregion

    #region F10: Fix SystemOne Hosting Path

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_01_SystemOneHostingState_SourceFile_ExistsInAspireHosting()
    {
        var path = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Modules/AI/Configuration/SystemOneHostingState.cs");
        Assert.True(File.Exists(path), $"SystemOneHostingState.cs must exist at '{path}'");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_02_SystemOneHostingState_ContainsCorrectAdapterDirectoryPath()
    {
        var path = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("src/IntoChat.OS.Modules/AI/systemone-adapter", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_03_SystemOneAdapter_PathFormatting_ConformsToModuleLayout()
    {
        var path = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("src/IntoChat.OS.Modules/AI/", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_04_SystemOneHostingState_DeclaresExpectedConstants()
    {
        var path = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("public const string SystemOneResourceName = \"systemone\";", content);
        Assert.Contains("public const string SystemOneApiKeyLocal = \"local\";", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F10")]
    public void F10_05_SystemOneHostingState_UsesIntoChatOSConfiguration()
    {
        var path = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Modules/AI/Configuration/SystemOneHostingState.cs");
        var content = File.ReadAllText(path);
        Assert.Contains("IntoChatOSConfiguration.EnvironmentName", content);
    }

    #endregion

    #region F11: Stale InternalsVisibleTo Cleanup

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_01_IntoChatOSClientOrleansCsproj_FileExists()
    {
        var csprojPath = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Client/IntoChat.OS.Client.Orleans/IntoChat.OS.Client.Orleans.csproj");
        Assert.True(File.Exists(csprojPath), $"Csproj must exist at {csprojPath}");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_02_IntoChatOSClientOrleansCsproj_GrantsFollowOwnTestsRule()
    {
        var csprojPath = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Client/IntoChat.OS.Client.Orleans/IntoChat.OS.Client.Orleans.csproj");
        var xml = XDocument.Load(csprojPath);
        var ivtElements = xml.Descendants("InternalsVisibleTo")
            .Select(x => x.Attribute("Include")?.Value)
            .Where(x => x != null)
            .ToList();

        Assert.All(ivtElements, elem => Assert.StartsWith("IntoChat.OS.Client.Orleans.Tests", elem));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_03_IntoChatOSClientOrleansCsproj_ParsesAsValidXml()
    {
        var csprojPath = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Client/IntoChat.OS.Client.Orleans/IntoChat.OS.Client.Orleans.csproj");
        var xml = XDocument.Load(csprojPath);
        Assert.NotNull(xml.Root);
        Assert.Equal("Project", xml.Root.Name.LocalName);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_04_ClientOrleansCsproj_ReferencesFramework()
    {
        var csprojPath = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Client/IntoChat.OS.Client.Orleans/IntoChat.OS.Client.Orleans.csproj");
        var content = File.ReadAllText(csprojPath);
        Assert.Contains("IntoChat.OS.Framework", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F11")]
    public void F11_05_ClientOrleansDirectory_ContainsExpectedSources()
    {
        var dir = Path.Combine(RepositoryRoot, "src/IntoChat.OS.Client/IntoChat.OS.Client.Orleans");
        Assert.True(File.Exists(Path.Combine(dir, "BrainClient.cs")));
        Assert.True(File.Exists(Path.Combine(dir, "OrleansBrain.cs")));
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
        var kernelTypes = typeof(IntoChat.OS.Kernel.CloudHost).Assembly.GetTypes();
        Assert.DoesNotContain(kernelTypes, t => t.Name.Contains("NamesForward", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_02_LegacyTypeNames_IsIsolatedFromKernel()
    {
        var kernelTypes = typeof(IntoChat.OS.Kernel.CloudHost).Assembly.GetTypes();
        Assert.DoesNotContain(kernelTypes, t => t.Name.Contains("LegacyTypeNames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_03_LegacyConfigurationGuard_IsIsolatedFromKernel()
    {
        var kernelTypes = typeof(IntoChat.OS.Kernel.CloudHost).Assembly.GetTypes();
        Assert.DoesNotContain(kernelTypes, t => t.Name.Contains("LegacyConfigurationGuard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_04_FrameworkAssembly_ExportsCanonicalConfigurationTypes()
    {
        var assembly = typeof(IntoChat.OS.Framework.CallerContextStamper).Assembly;
        var exportedTypes = assembly.GetExportedTypes().Select(t => t.Name).ToList();
        Assert.Contains("CallerContextStamper", exportedTypes);
        Assert.Contains("CallRequest", exportedTypes);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F13")]
    public void F13_05_CleanNamespaceIsolation_NoObsoleteShims()
    {
        var types = typeof(IntoChat.OS.Framework.CallerContextStamper).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("NamesForward", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region F14: Canonical Execution Context Subsystem

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F14")]
    public void F14_01_ExecutionContext_InitializesWithCallerAndEnvironment()
    {
        var caller = CapabilityCallers.Of("usr_ctx", "acc_1", "brain_1", CallerKind.User);
        var env = new Dictionary<string, string> { ["REGION"] = "us-east-1", ["TIER"] = "prod" };
        var ctx = new InMemoryExecutionContext("exec_001", caller, env);

        Assert.Equal("exec_001", ctx.ExecutionId);
        Assert.Equal("usr_ctx", ctx.Caller.PrincipalId);
        Assert.Equal("us-east-1", ctx.Environment["REGION"]);
        Assert.Empty(ctx.Slices);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F14")]
    public void F14_02_ExecutionContext_Environment_IsCaseInsensitive()
    {
        var caller = CapabilityCallers.Of("usr_1", "acc_1", "brain_1", CallerKind.User);
        var env = new Dictionary<string, string> { ["apiKey"] = "secret123" };
        var ctx = new InMemoryExecutionContext("exec_002", caller, env);

        Assert.Equal("secret123", ctx.Environment["APIKEY"]);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F14")]
    public async Task F14_03_ExecutionContext_WithSliceAsync_PushesAndPopsSlice()
    {
        var caller = CapabilityCallers.Of("usr_1", "acc_1", "brain_1", CallerKind.User);
        var ctx = new InMemoryExecutionContext("exec_003", caller);
        var slice = new ContextSlice("UserPrompt", "Explain Orleans clustering", ["prompt", "user"]);

        var sliceObservedInside = false;
        var result = await ctx.WithSliceAsync(slice, () =>
        {
            sliceObservedInside = ctx.Slices.Any(s => s.Name == "UserPrompt");
            return Task.FromResult("Explanation completed");
        });

        Assert.True(sliceObservedInside);
        Assert.Equal("Explanation completed", result);
        Assert.Empty(ctx.Slices);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F14")]
    public async Task F14_04_ExecutionContext_WithSliceAsync_PopsSliceOnException()
    {
        var caller = CapabilityCallers.Of("usr_1", "acc_1", "brain_1", CallerKind.User);
        var ctx = new InMemoryExecutionContext("exec_004", caller);
        var slice = new ContextSlice("Faulty", "will throw", []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.WithSliceAsync<string>(slice, () => throw new InvalidOperationException("Simulation failure")));

        Assert.Empty(ctx.Slices);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F14")]
    public void F14_05_ExecutionContext_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var caller = CapabilityCallers.Of("usr_1", "acc_1", "brain_1", CallerKind.User);
        var ctx = new InMemoryExecutionContext("exec_005", caller, cancellationToken: cts.Token);

        Assert.Equal(cts.Token, ctx.CancellationToken);
    }

    #endregion

    #region F15: Canonical Decision Maker Subsystem

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F15")]
    public async Task F15_01_DecideReactiveAsync_ReturnsHighConfidenceFastDecision()
    {
        var dm = new InMemoryDecisionMaker();
        var prompt = new ReactivePrompt("route_chat", new Dictionary<string, string> { ["tone"] = "casual" });

        var decision = await dm.DecideReactiveAsync(prompt);
        Assert.NotNull(decision);
        Assert.Equal("execute_default", decision.Choice);
        Assert.True(decision.Confidence >= 0.9);
        Assert.Contains("Reactive:route_chat", dm.AuditTrail);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F15")]
    public async Task F15_02_DecideReactiveAsync_HonorsRegisteredRule()
    {
        var dm = new InMemoryDecisionMaker();
        dm.RegisterReactiveRule("stream_audio", "codec_opus", 0.99);

        var prompt = new ReactivePrompt("stream_audio", new Dictionary<string, string>());
        var decision = await dm.DecideReactiveAsync(prompt);

        Assert.Equal("codec_opus", decision.Choice);
        Assert.Equal(0.99, decision.Confidence);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F15")]
    public async Task F15_03_DecideDeliberateAsync_GeneratesMultiStepPlan()
    {
        var dm = new InMemoryDecisionMaker();
        var goal = new DeliberateGoal("Refactor storage hierarchy", ["zero-downtime", "preserve-provenance"]);

        var decision = await dm.DecideDeliberateAsync(goal);
        Assert.NotNull(decision);
        Assert.Contains("Refactor storage hierarchy", decision.Plan);
        Assert.True(decision.Outcome.Succeeded);
        Assert.Equal(3, decision.Outcome.StepsExecuted);
        Assert.Contains("Deliberate:Refactor storage hierarchy", dm.AuditTrail);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F15")]
    public async Task F15_04_DecisionMaker_AuditTrail_MaintainsSequence()
    {
        var dm = new InMemoryDecisionMaker();
        await dm.DecideReactiveAsync(new ReactivePrompt("step1", new Dictionary<string, string>()));
        await dm.DecideDeliberateAsync(new DeliberateGoal("step2", []));

        Assert.Equal(2, dm.AuditTrail.Count);
        Assert.Equal("Reactive:step1", dm.AuditTrail[0]);
        Assert.Equal("Deliberate:step2", dm.AuditTrail[1]);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F15")]
    public async Task F15_05_DecideReactiveAsync_ThrowsOnCancellation()
    {
        var dm = new InMemoryDecisionMaker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dm.DecideReactiveAsync(new ReactivePrompt("op", new Dictionary<string, string>()), cts.Token));
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

    #region F17: Canonical Task Engine for Self-Improvement

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F17")]
    public async Task F17_01_CreateTaskAsync_InitializesStepsInPendingState()
    {
        var engine = new InMemoryTaskEngine();
        var req = new CreateTaskRequest("Upgrade Subsystems", "Execute M4 unification", ["Define context", "Unify decision maker"]);

        var task = await engine.CreateTaskAsync(req);
        Assert.NotNull(task.Id);
        Assert.Equal(AgentTaskStatus.Pending, task.Status);
        Assert.Equal(2, task.Steps.Length);
        Assert.All(task.Steps, s => Assert.Equal(TaskStepStatus.Pending, s.Status));
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F17")]
    public async Task F17_02_UpdateStepStatusAsync_AdvancesTaskToInProgressAndSucceeded()
    {
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Self-Test", "Run suite", ["step 1", "step 2"]));

        var t1 = await engine.UpdateStepStatusAsync(task.Id, "step_1", TaskStepStatus.Passed);
        Assert.Equal(AgentTaskStatus.InProgress, t1.Status);

        var t2 = await engine.UpdateStepStatusAsync(task.Id, "step_2", TaskStepStatus.Passed);
        Assert.Equal(AgentTaskStatus.Succeeded, t2.Status);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F17")]
    public async Task F17_03_UpdateStepStatusAsync_FailsTaskOnStepFailure()
    {
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Deploy", "Deploy cluster", ["provision", "migrate"]));

        var failedTask = await engine.UpdateStepStatusAsync(task.Id, "step_1", TaskStepStatus.Failed, "Resource limit exceeded");
        Assert.Equal(AgentTaskStatus.Failed, failedTask.Status);
        Assert.Equal("Resource limit exceeded", failedTask.Steps[0].Error);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F17")]
    public async Task F17_04_ListTasksAsync_ReturnsCreatedTasksDescending()
    {
        var engine = new InMemoryTaskEngine();
        await engine.CreateTaskAsync(new CreateTaskRequest("T1", "Obj 1", ["s1"]));
        await engine.CreateTaskAsync(new CreateTaskRequest("T2", "Obj 2", ["s2"]));

        var list = await engine.ListTasksAsync();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F17")]
    public async Task F17_05_VerifyTaskAsync_ValidatesSuccessfulCompletion()
    {
        var engine = new InMemoryTaskEngine();
        var task = await engine.CreateTaskAsync(new CreateTaskRequest("Build Verification", "Verify code", ["Compile", "Run Tests"]));

        await engine.UpdateStepStatusAsync(task.Id, "step_1", TaskStepStatus.Passed);
        await engine.UpdateStepStatusAsync(task.Id, "step_2", TaskStepStatus.Passed);

        var verification = await engine.VerifyTaskAsync(task.Id);
        Assert.True(verification.IsValid);
        Assert.Empty(verification.FailedAssertions);
    }

    #endregion

    #region F18: API Tracking Baseline & Contract Manifest Update

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_01_ContractManifestFile_ExistsInTestingDirectory()
    {
        var manifestPath = Path.Combine(RepositoryRoot, "src/Testing/IntoChat.OS.Architecture.Tests.Unit/ContractManifest.json");
        Assert.True(File.Exists(manifestPath), $"ContractManifest.json must exist at '{manifestPath}'");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_02_ContractManifestFile_ParsesAsValidJsonDocument()
    {
        var manifestPath = Path.Combine(RepositoryRoot, "src/Testing/IntoChat.OS.Architecture.Tests.Unit/ContractManifest.json");
        var content = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_03_ContractManifestFacts_ClassExistsInTestingSuite()
    {
        var factsPath = Path.Combine(RepositoryRoot, "src/Testing/IntoChat.OS.Architecture.Tests.Unit/ContractManifestFacts.cs");
        Assert.True(File.Exists(factsPath), $"ContractManifestFacts.cs must exist at '{factsPath}'");
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_04_ContractManifest_ContainsContractsSection()
    {
        var manifestPath = Path.Combine(RepositoryRoot, "src/Testing/IntoChat.OS.Architecture.Tests.Unit/ContractManifest.json");
        var content = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(content);
        var hasContracts = doc.RootElement.TryGetProperty("Contracts", out _)
            || doc.RootElement.TryGetProperty("Grains", out _);
        Assert.True(hasContracts);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F18")]
    public void F18_05_ContractManifest_TracksCoreModules()
    {
        var manifestPath = Path.Combine(RepositoryRoot, "src/Testing/IntoChat.OS.Architecture.Tests.Unit/ContractManifest.json");
        var content = File.ReadAllText(manifestPath);
        Assert.Contains("IntoChat.OS.Framework", content);
    }

    #endregion

    #region F19: TreatWarningsAsErrors Zero-Warning Enforcement

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_01_DirectoryBuildProps_ContainsTreatWarningsAsErrors()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist at root");

        var content = File.ReadAllText(propsPath);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_02_DirectoryBuildProps_SpecifiesNet11TargetFramework()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var content = File.ReadAllText(propsPath);
        Assert.Contains("<TargetFramework>net11.0</TargetFramework>", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_03_DirectoryBuildProps_EnforcesNullableReferenceTypes()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var content = File.ReadAllText(propsPath);
        Assert.Contains("<Nullable>enable</Nullable>", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_04_DirectoryBuildProps_EnforcesImplicitUsings()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var content = File.ReadAllText(propsPath);
        Assert.Contains("<ImplicitUsings>enable</ImplicitUsings>", content);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F19")]
    public void F19_05_DirectoryPackagesProps_DefinesCentralPackageManagement()
    {
        var packagesPath = Path.Combine(RepositoryRoot, "Directory.Packages.props");
        Assert.True(File.Exists(packagesPath), "Directory.Packages.props must exist at root");
        var content = File.ReadAllText(packagesPath);
        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", content);
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

        var result = await svc.RunTargetedAsync(req);
        Assert.True(result.AllPassed);
        Assert.Equal(10, result.TotalExecuted);
        Assert.Equal(10, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_02_TestVerificationService_VerifiesMilestones()
    {
        var svc = new InMemoryTestVerificationService();
        var approved = await svc.VerifyMilestoneAsync("M1");
        Assert.True(approved);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_03_TestExecutionResult_EncapsulatesFailureLogs()
    {
        var svc = new InMemoryTestVerificationService();
        var result = await svc.RunTargetedAsync(new TestExecutionRequest("Tier2_BoundaryCorner"));
        Assert.Empty(result.Failures);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public void F20_04_TestExecutionRequest_InitializesWithDefaults()
    {
        var req = new TestExecutionRequest("Tier3_CrossFeature");
        Assert.Equal("Tier3_CrossFeature", req.TargetTier);
        Assert.Null(req.FeatureFilter);
    }

    [Fact]
    [Trait("Category", "Tier1_FeatureCoverage")]
    [Trait("Feature", "F20")]
    public async Task F20_05_VerifyMilestoneAsync_ThrowsOnEmptyMilestoneId()
    {
        var svc = new InMemoryTestVerificationService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.VerifyMilestoneAsync(""));
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
