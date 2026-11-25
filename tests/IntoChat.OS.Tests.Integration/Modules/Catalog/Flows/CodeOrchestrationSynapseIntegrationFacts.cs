using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class CodeOrchestrationSynapseIntegrationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CleanNotificationScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                await os.Notifications.NotifyAsync("Digest", "Daily summary ready", NotificationPriority.Normal, ct);
                return new OrchestrationResult(
                    Status: "succeeded",
                    Summary: "Delivered digest notification.",
                    Artifacts: [new OrchestrationArtifact("digest.md", "markdown", "# Daily Digest")],
                    Metrics: new Dictionary<string, double> { ["delivered"] = 1 });
            }
        }
        """;

    private sealed class ScriptedModel(params string[] replies) : ICodeOrchestrationModel
    {
        private int _idx;
        public int Calls { get; private set; }

        public Task<string> GenerateScriptAsync(CodeOrchestrationPrompt prompt, CancellationToken ct = default)
        {
            Calls++;
            var reply = _idx < replies.Length ? replies[_idx] : replies[^1];
            _idx++;
            return Task.FromResult(reply);
        }
    }

    private sealed class RecordingSandboxBridge(Func<int, SandboxDispatchResult> onDispatch) : ISandboxRuntimeBridge
    {
        private int _dispatchCount;
        public List<string[]> DeployedScopes { get; } = [];
        public List<string> UnloadedApps { get; } = [];

        public Task<SandboxDeploymentResult> DeployPackageAsync(
            string brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));

        public Task<SandboxDeploymentResult> DeployScopedPackageAsync(
            BrainId brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, IReadOnlyList<string> allowedContracts, CancellationToken cancellationToken = default)
        {
            DeployedScopes.Add([.. allowedContracts]);
            return Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));
        }

        public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(false, null, null, null, "not used"));

        public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(false, null, null, null, "not used"));

        public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default)
        {
            var attempt = ++_dispatchCount;
            return Task.FromResult(onDispatch(attempt));
        }

        public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.FromResult<DigitalBrain.UI.View?>(null);

        public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
        {
            UnloadedApps.Add(appKey);
            return Task.CompletedTask;
        }
    }

    private static TestOS CreateTestOs(ISandboxRuntimeBridge bridge, ICodeOrchestrationModel? model = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AppPackageCompiler());
        services.AddSingleton<ICodeOrchestrationAstSanitizer, CodeOrchestrationAstSanitizer>();
        services.AddSingleton(bridge);
        if (model is not null)
        {
            services.AddSingleton(model);
        }

        return TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<CodeOrchestrationSynapse>();
    }

    [Fact]
    public async Task CodeOrchestrationSynapseCompletesEndToEndOnTestOSWithRepairAndResultJson()
    {
        const string brokenAttempt = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    int broken = "oops";
                    return Task.FromResult(new OrchestrationResult("succeeded", broken.ToString()));
                }
            }
            """;

        var model = new ScriptedModel(brokenAttempt, CleanNotificationScript);
        var resultPayload = JsonSerializer.Serialize("""
            {
              "status": "succeeded",
              "summary": "Delivered digest notification.",
              "artifacts": [{ "name": "digest.md", "kind": "markdown", "content": "# Daily Digest" }],
              "metrics": { "delivered": 1 }
            }
            """);
        var bridge = new RecordingSandboxBridge(_ => new SandboxDispatchResult(true, resultPayload, null, null, null));

        await using var os = CreateTestOs(bridge, model);
        var brain = new BrainId("orch-owner");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-e2e-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Send daily digest notification",
            AllowedContracts = ["github", "notifications"],
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Daily Digest", "owner", null, innerJson);
        await os.Idle();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        Assert.Equal(["generate", "approve", "execute", "finalize"], head.Done);

        var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.Equal(1, inner.RepairRoundsUsed);
        Assert.Equal(2, model.Calls);
        // Least-privilege scope narrowed strictly to the referenced "notifications" contract.
        Assert.Equal(["notifications"], Assert.Single(bridge.DeployedScopes));
        Assert.Single(bridge.UnloadedApps);
        Assert.NotNull(inner.Result);
        Assert.Equal("succeeded", inner.Result!.Status);
        Assert.Equal("Delivered digest notification.", inner.Result.Summary);
        Assert.Single(inner.Result.Artifacts);
        Assert.Contains(inner.Result.Metrics, m => m.Name == "delivered" && m.Value == 1);
    }

    [Fact]
    public async Task CodeOrchestrationSynapseDurablySuspendsOnPersonOnlyApprovalAndResumesWhenApproved()
    {
        var approvedPayload = JsonSerializer.Serialize("""
            {
              "status": "succeeded",
              "summary": "Executed gated action after Person confirmation.",
              "artifacts": [],
              "metrics": { "confirmedCalls": 1 }
            }
            """);

        // Attempt 1 returns a [PersonOnly] approval requirement; Attempt 2 (after approval) succeeds.
        var bridge = new RecordingSandboxBridge(attempt => attempt == 1
            ? new SandboxDispatchResult(false, null, null, null, "confirmation.person-only: Deleting repository branch requires person approval.")
            : new SandboxDispatchResult(true, approvedPayload, null, null, null));

        await using var os = CreateTestOs(bridge);
        var brain = new BrainId("orch-confirm");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-suspend-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Run gated orchestration",
            AllowedContracts = ["notifications"],
            PreauthoredScript = CleanNotificationScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Gated Action", "owner", null, innerJson);
        await os.Idle();

        // Flow must be durably suspended waiting for Person approval.
        var suspended = await starter.Head();
        Assert.Equal(FlowStatus.Waiting, suspended.Status);

        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var snapshot = await inbox.Read();
        var request = Assert.Single(snapshot.Pending, p => p.Plan.Kind == "code-orchestration.person-only");
        Assert.True(request.Plan.RequiresPerson);

        await inbox.Resolve(request.Plan.Id, approve: true);
        await os.Idle();

        var completed = await starter.Head();
        Assert.Equal(FlowStatus.Completed, completed.Status);
        var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(completed.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.True(inner.Approved);
        Assert.NotNull(inner.Result);
        Assert.Equal("succeeded", inner.Result!.Status);
        Assert.Equal(2, bridge.DeployedScopes.Count);
        Assert.Equal(2, bridge.UnloadedApps.Count);
    }

    [Fact]
    public async Task CodeOrchestrationSynapseEndsWithoutReExecutingWhenPersonDeniesApproval()
    {
        var bridge = new RecordingSandboxBridge(_ =>
            new SandboxDispatchResult(false, null, null, null, "ApprovalRequired: Action requires person approval."));

        await using var os = CreateTestOs(bridge);
        var brain = new BrainId("orch-deny");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-deny-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Denied gated action",
            AllowedContracts = ["notifications"],
            PreauthoredScript = CleanNotificationScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Denied Action", "owner", null, innerJson);
        await os.Idle();

        var suspended = await starter.Head();
        Assert.Equal(FlowStatus.Waiting, suspended.Status);

        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var snapshot = await inbox.Read();
        var request = Assert.Single(snapshot.Pending, p => p.Plan.Kind == "code-orchestration.person-only");
        Assert.True(request.Plan.RequiresPerson);

        await inbox.Resolve(request.Plan.Id, approve: false);
        await os.Idle();

        var finished = await starter.Head();
        Assert.NotEqual(FlowStatus.Completed, finished.Status);
        Assert.NotEqual(FlowStatus.Waiting, finished.Status);
        // Sandbox must not have been re-executed after denial.
        Assert.Single(bridge.DeployedScopes);
    }

    [Fact]
    public async Task RealUnitHostSandboxExecutesOrchestrationScriptAndEnforcesScopedAppTokenOverEdgeBridge()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var options = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "integration-test-scoped-token-secret",
            TokenTtlMinutes = 15,
        });
        services.AddSingleton(options);
        services.AddSingleton<AppTokenService>();
        services.AddSingleton<SdkAiAdapter>();
        services.AddSingleton<SdkTablesAdapter>();
        await using var provider = services.BuildServiceProvider();

        await using var realBridge = new HttpSandboxRuntimeBridge(
            provider.GetRequiredService<IHttpClientFactory>(),
            options,
            provider.GetRequiredService<AppTokenService>(),
            provider,
            NullLogger<HttpSandboxRuntimeBridge>.Instance);

        var compiler = new AppPackageCompiler();

        // 1. Standalone net11.0 script executing in real --unit-host sandbox returning structured result.json.
        const string pureScript = """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    using var doc = JsonDocument.Parse(inputJson);
                    var n = doc.RootElement.TryGetProperty("count", out var prop) ? prop.GetInt32() : 0;
                    return Task.FromResult(new OrchestrationResult(
                        Status: "succeeded",
                        Summary: $"Computed {n * 2} inside UnitHost",
                        Artifacts: [new OrchestrationArtifact("output.json", "json", $"{{\"doubled\":{n * 2}}}")],
                        Metrics: new Dictionary<string, double> { ["doubled"] = n * 2 }));
                }
            }
            """;

        await using var os = CreateTestOs(realBridge);
        var brain = new BrainId("orch-unithost");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-real-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Double count inside UnitHost sandbox",
            InputJson = """{"count":21}""",
            AllowedContracts = ["notifications"],
            PreauthoredScript = pureScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("UnitHost Compute", "owner", null, innerJson);
        await os.Idle();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.NotNull(inner.Result);
        Assert.Equal("succeeded", inner.Result!.Status);
        Assert.Equal("Computed 42 inside UnitHost", inner.Result.Summary);
        Assert.Contains(inner.Result.Metrics, m => m.Name == "doubled" && m.Value == 42);

        // 2. Scoped AppToken enforcement inside real --unit-host: token scoped to ["github"] rejects INotifications with 403.
        const string outOfScopeCallScript = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    _ = await os.Notifications.ListAsync(5, false, ct);
                    return new OrchestrationResult("succeeded", "should never succeed");
                }
            }
            """;

        var appKey = CodeOrchestrationScriptWrapper.AppKey("scoped-deny-unithost");
        var compiled = compiler.Compile(CodeOrchestrationScriptWrapper.BuildPackage(appKey, "Scoped Deny", outOfScopeCallScript));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));

        await realBridge.DeployScopedPackageAsync(
            brain,
            compiled.EnrichedPackage!,
            compiled.AssemblyBytes,
            stateJson: null,
            settingsJson: null,
            allowedContracts: ["github"],
            cancellationToken: Ct);

        try
        {
            var dispatch = await realBridge.DispatchCommandAsync(
                brain.Value,
                appKey,
                CodeOrchestrationScriptWrapper.Command,
                JsonSerializer.Serialize("{}"),
                Ct);

            Assert.NotNull(dispatch.Error);
            Assert.Contains("IntoChat.Sdk.INotifications", dispatch.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outside the scoped AppToken", dispatch.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await realBridge.UnloadAsync(brain.Value, appKey, Ct);
        }
    }
}
