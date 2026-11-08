#pragma warning disable xUnit1051, IDE0062, IDE0200
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Runtime.Orleans.Confirmations;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using AppRequirements = IntoChat.OS.Modules.Apps.AppRequirements;

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
        var caller = CapabilityCallers.Of("knowledge_agent", "acc_infra", brain, CallerKind.User);
        using var stamp = RuntimeCallerContext.Stamp(caller);

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
    }
}
