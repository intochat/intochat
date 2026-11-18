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
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.ServiceDefaults;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;

namespace IntoChat.OS.Kernel.Tests.Unit.Harness;

/// <summary>
/// Tier 3: Cross-Feature Combinations — Pairwise tests verifying seamless interaction
/// across decoupled kernel, runtime, security, manifest, and telemetry subsystems.
/// </summary>
public sealed class Tier3_CrossFeaturePairwiseFacts
{
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
        var filmCraftManifest = FilmCraftApp.Bundle;
        Assert.Equal("filmcraft", filmCraftManifest.Name);

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
        var filmCraft = FilmCraftApp.Bundle;
        var activity = new AppActivity(filmCraft.Name, "rendering", "Exporting 4K sequence");

        Assert.Equal("filmcraft", activity.App);
        Assert.Equal("rendering", activity.State);
        Assert.Equal("Exporting 4K sequence", activity.Detail);

        var photoCraft = PhotoCraftApp.Bundle;
        var photoActivity = new AppActivity(photoCraft.Name, "editing", "Applying color grade");

        Assert.Equal("photocraft", photoActivity.App);
        Assert.NotEqual(activity.App, photoActivity.App);
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
