using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.CodeBlocks;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Workspace.Tests.Unit.CodeBlocks;

public sealed class CodeBlockConnectedComputeFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SimpleBlockSource = """
        public static class Block
        {
            public static string Run(string input) => $"echo:{input}";
        }
        """;

    [Fact]
    public async Task CodeBlockRunRoutesToTier1ConnectedPcWhenOnlineAndFallsBackWhenOffline()
    {
        var brain = new BrainId("owner");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        var pcCalls = 0;

        var transport = new ScriptedComputeTransport(
            probe: (_, _, _) => Task.FromResult(true),
            execute: (_, node, _, _) =>
            {
                pcCalls++;
                return Task.FromResult<ConnectedSandboxResponse?>(
                    new ConnectedSandboxResponse(
                        Handled: true,
                        ResultJson: JsonSerializer.Serialize("from-connected-pc"),
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
            .AddSingleton<ISandboxRuntimeBridge>(bridge)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = osServices)
            .Register<CodeBlockRunNeuron>()
            .Register<ClientGrantsNeuron>();

        // 1. Tier 1 Connected PC online -> routes to connected-client
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            var run1 = os.Get<ICodeBlockRun>(new NeuronId(brain, "codeblock.run", "codeblocks/tier1-test"));
            var view1 = await run1.Start("Tier 1 test", SimpleBlockSource, "hello");
            Assert.Equal(CodeBlockStatus.Completed, view1.Status);
            Assert.Equal("from-connected-pc", view1.Output);
            Assert.Equal("connected-client", view1.ExecutionTarget);
            Assert.Equal("win-pc-tier1", view1.ExecutedByMachineId);
            Assert.Contains("tier:connected-client", view1.Steps);
            Assert.Equal(1, pcCalls);
        }

        // 2. Mark PC offline -> falls back to Tier 2 (using a stub fallback bridge)
        await router.MarkOfflineAsync(brain, "win-pc-tier1", Ct);
        var fallbackBridge = new FallbackSandboxBridge();
        var fallbackServices = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton<ISandboxRuntimeBridge>(fallbackBridge)
            .BuildServiceProvider();

        await using var fallbackOs = TestOS.Local(o => o.Services = fallbackServices)
            .Register<CodeBlockRunNeuron>()
            .Register<ClientGrantsNeuron>();

        using (fallbackOs.Edge.Enter(fallbackOs.Edge.Person("owner", "owner", brain)))
        {
            var run2 = fallbackOs.Get<ICodeBlockRun>(new NeuronId(brain, "codeblock.run", "codeblocks/tier2-test"));
            var view2 = await run2.Start("Tier 2 fallback test", SimpleBlockSource, "hello");
            Assert.Equal(CodeBlockStatus.Completed, view2.Status);
            Assert.Equal("from-tier2-fallback", view2.Output);
            Assert.Equal("cloud-sandbox", view2.ExecutionTarget);
            Assert.Null(view2.ExecutedByMachineId);
            Assert.Contains("tier:cloud-sandbox", view2.Steps);
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
            => Task.FromResult(new SandboxDeploymentResult(true, null, [], [], ["run"], null));

        public Task<SandboxDispatchResult> ExecuteScopedCommandAsync(BrainId brainId, AppPackage package, byte[] assemblyBytes, string command, string? payloadJson, IReadOnlyList<string> allowedContracts, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, JsonSerializer.Serialize("from-tier2-fallback"), null, null, null, ExecutionTarget: "cloud-sandbox", ExecutedByMachineId: null));

        public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, null, null, null, null));

        public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, null, null, null, null));

        public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(true, null, null, null, null));

        public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.FromResult<DigitalBrain.UI.View?>(null);

        public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
