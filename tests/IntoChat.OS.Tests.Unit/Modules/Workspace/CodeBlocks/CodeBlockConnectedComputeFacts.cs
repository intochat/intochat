using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Workspace.Tests.Unit.CodeBlocks;

public sealed class CodeBlockConnectedComputeFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SimpleOrchestrationScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                => Task.FromResult(new OrchestrationResult(
                    Status: "succeeded",
                    Summary: $"echo:{inputJson}",
                    Artifacts: [],
                    Metrics: new Dictionary<string, double>()));
        }
        """;

    [Fact]
    public async Task CodeOrchestrationRoutesToTier1ConnectedPcWhenOnlineAndFallsBackWhenOffline()
    {
        var brain = new BrainId("owner");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        var pcCalls = 0;

        var pcResultJson = JsonSerializer.Serialize(new
        {
            status = "succeeded",
            summary = "from-connected-pc",
            artifacts = Array.Empty<object>(),
            metrics = new Dictionary<string, double>(),
        });

        var transport = new ScriptedComputeTransport(
            probe: (_, _, _) => Task.FromResult(true),
            execute: (_, node, _, _) =>
            {
                pcCalls++;
                return Task.FromResult<ConnectedSandboxResponse?>(
                    new ConnectedSandboxResponse(
                        Handled: true,
                        ResultJson: pcResultJson,
                        UpdatedStateJson: null,
                        Error: null,
                        ExecutedByMachineId: node.MachineId));
            });

        var router = new ConnectedComputeRouter(clock: clock, transport: transport);
        await router.RecordHeartbeatAsync(
            brain,
            new ComputeCapabilityAdvertisement(
                MachineId: "win-pc-tier1",
                DeviceName: "My Windows PC",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow(),
                AllowClientCodeSandbox: true,
                AdvertisedCapabilities: [ICodeSandbox.CapabilityId]),
            Ct);

        var services = new ServiceCollection();
        services.AddHttpClient();
        var sandboxOptions = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "unit-test-token-signing-material",
            TokenTtlMinutes = 15,
        });
        services.AddSingleton(sandboxOptions);
        services.AddSingleton<AppTokenService>();
        await using var sp = services.BuildServiceProvider();

        await using var bridge = new HttpSandboxRuntimeBridge(
            sp.GetRequiredService<IHttpClientFactory>(),
            sandboxOptions,
            sp.GetRequiredService<AppTokenService>(),
            sp,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router);

        var osServices = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton<ICodeOrchestrationAstSanitizer>(new CodeOrchestrationAstSanitizer())
            .AddSingleton<ISandboxRuntimeBridge>(bridge)
            .AddSingleton<IConnectedComputeRouter>(router)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = osServices)
            .Register<CodeOrchestrationSynapse>();

        // 1. Tier 1 Connected PC online -> routes to connected-client
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            var run1 = os.Get<ICodeOrchestration>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "code-orchestration/tier1-test"));
            _ = await run1.Start("Tier 1 test", "Run on connected PC", SimpleOrchestrationScript, "{\"msg\":\"hello\"}");
            await os.Idle();
            var view1 = await run1.Read();
            Assert.NotNull(view1);
            Assert.Equal(FlowStatus.Completed, view1!.Status);
            Assert.Equal("from-connected-pc", view1.Result?.Summary);
            Assert.Equal("connected-client", view1.ExecutionTarget);
            Assert.Equal("win-pc-tier1", view1.ExecutedByMachineId);
            Assert.Equal(1, pcCalls);
        }

        // 2. Mark PC offline -> falls back to Tier 2 (using a stub fallback bridge)
        await router.MarkOfflineAsync(brain, "win-pc-tier1", Ct);
        var fallbackBridge = new FallbackSandboxBridge();
        var fallbackServices = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton<ICodeOrchestrationAstSanitizer>(new CodeOrchestrationAstSanitizer())
            .AddSingleton<ISandboxRuntimeBridge>(fallbackBridge)
            .AddSingleton<IConnectedComputeRouter>(router)
            .BuildServiceProvider();

        await using var fallbackOs = TestOS.Local(o => o.Services = fallbackServices)
            .Register<CodeOrchestrationSynapse>();

        using (fallbackOs.Edge.Enter(fallbackOs.Edge.Person("owner", "owner", brain)))
        {
            var run2 = fallbackOs.Get<ICodeOrchestration>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "code-orchestration/tier2-test"));
            _ = await run2.Start("Tier 2 fallback test", "Run on fallback sandbox", SimpleOrchestrationScript, "{\"msg\":\"hello\"}");
            await fallbackOs.Idle();
            var view2 = await run2.Read();
            Assert.NotNull(view2);
            Assert.Equal(FlowStatus.Completed, view2!.Status);
            Assert.Equal("from-local-unit-host", view2.Result?.Summary);
            Assert.Equal("local-unit-host", view2.ExecutionTarget);
            Assert.Null(view2.ExecutedByMachineId);
        }
    }

    private sealed class ScriptedComputeTransport(
        Func<BrainId, ConnectedComputeNode, CancellationToken, Task<bool>> probe,
        Func<BrainId, ConnectedComputeNode, ConnectedSandboxRequest, CancellationToken, Task<ConnectedSandboxResponse?>> execute)
        : IConnectedComputeTransport
    {
        public Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
            => probe(brainId, node, ct);

        public Task<ConnectedSandboxResponse?> ExecuteAsync(BrainId brainId, ConnectedComputeNode node, ConnectedSandboxRequest request, CancellationToken ct = default)
            => execute(brainId, node, request, ct);
    }

    private sealed class FallbackSandboxBridge : ISandboxRuntimeBridge
    {
        public Task<SandboxDeploymentResult> DeployPackageAsync(string brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));

        public Task<SandboxDispatchResult> ExecuteScopedCommandAsync(BrainId brainId, AppPackage package, byte[] assemblyBytes, string command, string? payloadJson, IReadOnlyList<string> allowedContracts, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(
                true,
                JsonSerializer.Serialize(new
                {
                    status = "succeeded",
                    summary = "from-local-unit-host",
                    artifacts = Array.Empty<object>(),
                    metrics = new Dictionary<string, double>(),
                }),
                null,
                null,
                null,
                ExecutionTarget: "local-unit-host",
                ExecutedByMachineId: null));

        public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.FromResult<DigitalBrain.UI.View?>(null);

        public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
