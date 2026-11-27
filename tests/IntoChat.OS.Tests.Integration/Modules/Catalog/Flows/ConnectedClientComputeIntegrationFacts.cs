using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class ConnectedClientComputeIntegrationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PureComputeScript = """
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

    private const string OutOfScopeNotificationScript = """
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

    private static TestOS CreateTestOs(ISandboxRuntimeBridge bridge, IConnectedComputeRouter? computeRouter = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AppPackageCompiler());
        services.AddSingleton<ICodeOrchestrationAstSanitizer, CodeOrchestrationAstSanitizer>();
        services.AddSingleton(bridge);
        services.AddSingleton<ICodeSandbox>(bridge);
        if (computeRouter is not null)
        {
            services.AddSingleton(computeRouter);
        }

        return TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<CodeOrchestrationSynapse>();
    }

    [Fact]
    public async Task CodeOrchestrationSynapseRoutesToOnlineAutoAllConnectedClientUnitHostAndEnforcesScopedAppTokenOverEdgeBridge()
    {
        var brain = new BrainId("orch-sovereign");
        var edgeHits = new ConcurrentQueue<(string Path, bool HasAppToken)>();

        // 1. Start a real loopback HTTP edge gateway exposing /edge/sandbox/invoke with server-owned AppTokenService.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var serverSandboxOptions = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "integration-server-scoped-token-secret",
            TokenTtlMinutes = 15,
        });
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(serverSandboxOptions);
        builder.Services.AddSingleton<AppTokenService>();
        builder.Services.AddSingleton<SdkAiAdapter>();
        builder.Services.AddSingleton<SdkTablesAdapter>();

        await using var edgeApp = builder.Build();
        edgeApp.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/edge/sandbox", StringComparison.OrdinalIgnoreCase))
            {
                edgeHits.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Headers.ContainsKey("X-App-Token")));
            }

            await next();
        });
        SandboxEdgeEndpoints.Map(edgeApp);
        await edgeApp.StartAsync(Ct);

        try
        {
            var edgeUrl = edgeApp.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single()
                .TrimEnd('/');

            // 2. Client-side local sandbox executor (separate AppTokenService signing key so it cannot self-verify server tokens).
            var clientServices = new ServiceCollection();
            clientServices.AddHttpClient();
            var clientSandboxOptions = Options.Create(new SandboxOptions
            {
                TokenSigningMaterial = "different-client-only-secret",
                TokenTtlMinutes = 15,
                AllowLoopbackKernelCallback = true,
            });
            clientServices.AddSingleton(clientSandboxOptions);
            clientServices.AddSingleton<AppTokenService>();
            clientServices.AddSingleton<SdkAiAdapter>();
            clientServices.AddSingleton<SdkTablesAdapter>();
            await using var clientProvider = clientServices.BuildServiceProvider();

            await using var clientBridge = new HttpSandboxRuntimeBridge(
                clientProvider.GetRequiredService<IHttpClientFactory>(),
                clientSandboxOptions,
                clientProvider.GetRequiredService<AppTokenService>(),
                clientProvider,
                NullLogger<HttpSandboxRuntimeBridge>.Instance);
            ILocalSandboxExecutor clientExecutor = clientBridge;

            // 3. Register owner-desktop-1 via P-256 authenticated heartbeat with ConsentMode = AutoAll.
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
            var transport = new DirectClientTransport(clientExecutor);
            var router = new ConnectedComputeRouter(clock: clock, transport: transport);
            using var desktopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var desktopSpki = desktopKey.ExportSubjectPublicKeyInfo();
            var capUrl = new Uri("https://relay.test" + DeviceRoutes.ComputeCapability);
            const string deviceAccessToken = "owner-desktop-1.access";
            var proof = MachineProof.Sign(desktopKey, "PUT", capUrl, deviceAccessToken, clock.GetUtcNow());
            var proofResult = await router.RecordAuthenticatedHeartbeatAsync(
                brain,
                desktopSpki,
                proof,
                "PUT",
                capUrl,
                deviceAccessToken,
                new HashSetNonceCache(),
                new ComputeCapabilityAdvertisement(
                    MachineId: "owner-desktop-1",
                    DeviceName: "Owner Workstation",
                    ConsentMode: ComputeConsentMode.AutoAll,
                    SandboxReady: true,
                    SandboxRuntime: "unit-host",
                    OsDescription: "Windows 11",
                    MaxConcurrency: 2,
                    HeartbeatAt: clock.GetUtcNow(),
                    KeyThumbprint: DeviceKeys.Thumbprint(desktopSpki)),
                Ct);
            Assert.True(proofResult.IsValid);

            // 4. Server-side sandbox bridge pointing its callback URL to the live HTTP /edge/sandbox/invoke gateway.
            var serverBridgeOptions = Options.Create(new SandboxOptions
            {
                KernelCallbackUrl = edgeUrl,
                TokenSigningMaterial = serverSandboxOptions.Value.TokenSigningMaterial,
                TokenTtlMinutes = 15,
            });
            await using var serverBridge = new HttpSandboxRuntimeBridge(
                edgeApp.Services.GetRequiredService<IHttpClientFactory>(),
                serverBridgeOptions,
                edgeApp.Services.GetRequiredService<AppTokenService>(),
                edgeApp.Services,
                NullLogger<HttpSandboxRuntimeBridge>.Instance,
                router);

            // 5. Run CodeOrchestrationSynapse end-to-end on TestOS -> must route to owner-desktop-1.
            await using var os = CreateTestOs(serverBridge);
            var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-connected-1");
            var starter = os.Get<IFlowStarter>(flowId);

            using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
            var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
            {
                Goal = "Execute sovereign compute on owner desktop",
                InputJson = """{"count":21}""",
                AllowedContracts = ["notifications"],
                PreauthoredScript = PureComputeScript,
            }, CodeOrchestrationSynapse.Json);

            await starter.StartFromJson("Sovereign Local Compute", "owner", null, innerJson);
            await os.Idle();

            var head = await starter.Head();
            Assert.Equal(FlowStatus.Completed, head.Status);
            var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
            Assert.Equal("connected-client", inner.ExecutionTarget);
            Assert.Equal("owner-desktop-1", inner.ExecutedByMachineId);
            Assert.NotNull(inner.Result);
            Assert.Equal("succeeded", inner.Result!.Status);
            Assert.Equal("Computed 42 inside UnitHost", inner.Result.Summary);
            Assert.Contains(inner.Result.Metrics, m => m.Name == "doubled" && m.Value == 42);
            Assert.Equal(1, transport.ExecuteCount);

            // 6. Verify connected client's --unit-host calls /edge/sandbox/invoke with scoped AppToken and out-of-scope calls get 403.
            var compiler = new AppPackageCompiler();
            var appKey = CodeOrchestrationScriptWrapper.AppKey("connected-scoped-deny");
            var compiled = compiler.Compile(CodeOrchestrationScriptWrapper.BuildPackage(appKey, "Scoped Deny", OutOfScopeNotificationScript));
            Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));

            var scopedDeny = await serverBridge.ExecuteScopedCommandAsync(
                brain,
                compiled.EnrichedPackage!,
                compiled.AssemblyBytes,
                CodeOrchestrationScriptWrapper.Command,
                JsonSerializer.Serialize("{}"),
                allowedContracts: ["github"],
                cancellationToken: Ct);

            Assert.Equal("connected-client", scopedDeny.ExecutionTarget);
            Assert.Equal("owner-desktop-1", scopedDeny.ExecutedByMachineId);
            Assert.NotNull(scopedDeny.Error);
            Assert.Contains("IntoChat.Sdk.INotifications", scopedDeny.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outside the scoped AppToken", scopedDeny.Error, StringComparison.OrdinalIgnoreCase);

            // Confirm the out-of-process UnitHost communicated strictly over HTTP /edge/sandbox/invoke with X-App-Token.
            Assert.Contains(edgeHits, hit => hit.Path == "/edge/sandbox/invoke" && hit.HasAppToken);
        }
        finally
        {
            await edgeApp.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CodeOrchestrationSynapseFallsBackTransparentlyToCloudSandboxWhenConnectedClientTimesOutUnder500Ms()
    {
        var brain = new BrainId("orch-cloud-fallback");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var devicesOptions = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions { AvailabilityTimeoutMs = 200 },
        });
        var hangingTransport = new HangingProbeTransport();
        var router = new ConnectedComputeRouter(options: devicesOptions, clock: clock, transport: hangingTransport);

        await router.RecordHeartbeatAsync(
            brain,
            new ComputeCapabilityAdvertisement(
                MachineId: "sleeping-desktop",
                DeviceName: "Sleeping Desktop",
                ConsentMode: ComputeConsentMode.AutoAll,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow()),
            Ct);

        var cloudHandler = new MockCloudSandboxHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("SandboxWorker").ConfigurePrimaryHttpMessageHandler(() => cloudHandler);
        var sandboxOptions = Options.Create(new SandboxOptions
        {
            Url = "https://lean-cloud-sandbox.test",
            TokenSigningMaterial = "integration-cloud-fallback-secret",
            TokenTtlMinutes = 15,
        });
        services.AddSingleton(sandboxOptions);
        services.AddSingleton<AppTokenService>();
        await using var provider = services.BuildServiceProvider();

        await using var bridge = new HttpSandboxRuntimeBridge(
            provider.GetRequiredService<IHttpClientFactory>(),
            sandboxOptions,
            provider.GetRequiredService<AppTokenService>(),
            provider,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router);

        await using var os = CreateTestOs(bridge);
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-cloud-fallback-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Fallback to cloud sandbox when local client hangs",
            InputJson = """{"count":7}""",
            AllowedContracts = ["notifications"],
            PreauthoredScript = PureComputeScript,
        }, CodeOrchestrationSynapse.Json);

        var sw = Stopwatch.StartNew();
        await starter.StartFromJson("Cloud Fallback", "owner", null, innerJson);
        await os.Idle();
        sw.Stop();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.Equal("cloud-sandbox", inner.ExecutionTarget);
        Assert.Null(inner.ExecutedByMachineId);
        Assert.NotNull(inner.Result);
        Assert.Equal("succeeded", inner.Result!.Status);
        Assert.Equal("Executed on cloud sandbox fallback.", inner.Result.Summary);
        Assert.Equal(["/apps/deploy", "/apps/dispatch", "/apps/unload"], cloudHandler.Paths);

        // Connected client that timed out (<500ms) must now be marked Offline.
        var node = Assert.Single(await router.ListNodesAsync(brain, Ct));
        Assert.Equal(ComputeNodeStatus.Offline, node.Status);
    }

    [Fact]
    public async Task CodeOrchestrationSynapseFallsBackTransparentlyToLocalUnitHostWhenNoAutoAllClientIsOnline()
    {
        var brain = new BrainId("orch-local-fallback");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var router = new ConnectedComputeRouter(clock: clock);

        // Only a Disabled-consent client is registered -> ineligible for sovereign compute.
        await router.RecordHeartbeatAsync(
            brain,
            new ComputeCapabilityAdvertisement(
                MachineId: "opted-out-laptop",
                DeviceName: "Opted-Out Laptop",
                ConsentMode: ComputeConsentMode.Disabled,
                SandboxReady: false,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow()),
            Ct);

        var services = new ServiceCollection();
        services.AddHttpClient();
        var sandboxOptions = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "integration-local-fallback-secret",
            TokenTtlMinutes = 15,
        });
        services.AddSingleton(sandboxOptions);
        services.AddSingleton<AppTokenService>();
        services.AddSingleton<SdkAiAdapter>();
        services.AddSingleton<SdkTablesAdapter>();
        await using var provider = services.BuildServiceProvider();

        await using var bridge = new HttpSandboxRuntimeBridge(
            provider.GetRequiredService<IHttpClientFactory>(),
            sandboxOptions,
            provider.GetRequiredService<AppTokenService>(),
            provider,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router);

        await using var os = CreateTestOs(bridge);
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-local-fallback-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Fallback to local --unit-host when no AutoAll client is online",
            InputJson = """{"count":42}""",
            AllowedContracts = ["notifications"],
            PreauthoredScript = PureComputeScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Local UnitHost Fallback", "owner", null, innerJson);
        await os.Idle();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.Equal("local-unit-host", inner.ExecutionTarget);
        Assert.Null(inner.ExecutedByMachineId);
        Assert.NotNull(inner.Result);
        Assert.Equal("succeeded", inner.Result!.Status);
        Assert.Equal("Computed 84 inside UnitHost", inner.Result.Summary);
        Assert.Contains(inner.Result.Metrics, m => m.Name == "doubled" && m.Value == 84);
    }

    [Fact]
    public async Task CodeOrchestrationSynapseRoutesToExplicitToggleConsentedCodeSandboxClientAndFallsBackWhenRevoked()
    {
        var brain = new BrainId("orch-explicit-toggle");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var nonces = new HashSetNonceCache();

        // Client-side local sandbox executor.
        var clientServices = new ServiceCollection();
        clientServices.AddHttpClient();
        var clientSandboxOptions = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "explicit-toggle-client-secret",
            TokenTtlMinutes = 15,
        });
        clientServices.AddSingleton(clientSandboxOptions);
        clientServices.AddSingleton<AppTokenService>();
        clientServices.AddSingleton<SdkAiAdapter>();
        clientServices.AddSingleton<SdkTablesAdapter>();
        await using var clientProvider = clientServices.BuildServiceProvider();

        await using var clientBridge = new HttpSandboxRuntimeBridge(
            clientProvider.GetRequiredService<IHttpClientFactory>(),
            clientSandboxOptions,
            clientProvider.GetRequiredService<AppTokenService>(),
            clientProvider,
            NullLogger<HttpSandboxRuntimeBridge>.Instance);

        var transport = new DirectClientTransport(clientBridge);
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        // Enroll desktop client over P-256 with ExplicitToggle + AllowClientCodeSandbox = true + ICodeSandbox.
        using var desktopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var desktopSpki = desktopKey.ExportSubjectPublicKeyInfo();
        var capUrl = new Uri("https://relay.test" + DeviceRoutes.ComputeCapability);
        const string deviceAccessToken = "owner-desktop-explicit.access";

        var enableProof = MachineProof.Sign(desktopKey, "PUT", capUrl, deviceAccessToken, clock.GetUtcNow());
        var enableResult = await router.RecordAuthenticatedHeartbeatAsync(
            brain,
            desktopSpki,
            enableProof,
            "PUT",
            capUrl,
            deviceAccessToken,
            nonces,
            new ComputeCapabilityAdvertisement(
                MachineId: "owner-desktop-explicit",
                DeviceName: "Owner Desktop (ExplicitToggle)",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow(),
                KeyThumbprint: DeviceKeys.Thumbprint(desktopSpki),
                AllowClientCodeSandbox: true,
                AdvertisedCapabilities: [ICodeSandbox.CapabilityId]),
            Ct);
        Assert.True(enableResult.IsValid);

        // Server-side sandbox bridge with router attached.
        var serverServices = new ServiceCollection();
        serverServices.AddHttpClient();
        var serverSandboxOptions = Options.Create(new SandboxOptions
        {
            TokenSigningMaterial = "explicit-toggle-server-secret",
            TokenTtlMinutes = 15,
        });
        serverServices.AddSingleton(serverSandboxOptions);
        serverServices.AddSingleton<AppTokenService>();
        serverServices.AddSingleton<SdkAiAdapter>();
        serverServices.AddSingleton<SdkTablesAdapter>();
        await using var serverProvider = serverServices.BuildServiceProvider();

        await using var serverBridge = new HttpSandboxRuntimeBridge(
            serverProvider.GetRequiredService<IHttpClientFactory>(),
            serverSandboxOptions,
            serverProvider.GetRequiredService<AppTokenService>(),
            serverProvider,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            router);

        await using var os = CreateTestOs(serverBridge, router);

        // 1. Consented device online -> CodeOrchestrationSynapse routes to connected client.
        using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var firstStarter = os.Get<IFlowStarter>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-explicit-consented"));
        var payloadJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Execute script on consented ExplicitToggle client",
            InputJson = """{"count":30}""",
            AllowedContracts = ["notifications"],
            PreauthoredScript = PureComputeScript,
        }, CodeOrchestrationSynapse.Json);

        await firstStarter.StartFromJson("ExplicitToggle Consented", "owner", null, payloadJson);
        await os.Idle();

        var firstHead = await firstStarter.Head();
        Assert.Equal(FlowStatus.Completed, firstHead.Status);
        var firstInner = JsonSerializer.Deserialize<CodeOrchestrationInner>(firstHead.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.Equal("connected-client", firstInner.ExecutionTarget);
        Assert.Equal("owner-desktop-explicit", firstInner.ExecutedByMachineId);
        Assert.Contains(ICodeSandbox.CapabilityId, firstInner.RouteReason ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Computed 60 inside UnitHost", firstInner.Result!.Summary);
        Assert.Equal(1, transport.ExecuteCount);

        // 2. Client revokes consent toggle over P-256 -> CodeOrchestrationSynapse falls back cleanly to local-unit-host.
        clock.Advance(TimeSpan.FromSeconds(2));
        var revokeProof = MachineProof.Sign(desktopKey, "PUT", capUrl, deviceAccessToken, clock.GetUtcNow());
        var revokeResult = await router.RecordAuthenticatedHeartbeatAsync(
            brain,
            desktopSpki,
            revokeProof,
            "PUT",
            capUrl,
            deviceAccessToken,
            nonces,
            new ComputeCapabilityAdvertisement(
                MachineId: "owner-desktop-explicit",
                DeviceName: "Owner Desktop (ExplicitToggle)",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: false,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow(),
                KeyThumbprint: DeviceKeys.Thumbprint(desktopSpki),
                AllowClientCodeSandbox: false,
                AdvertisedCapabilities: []),
            Ct);
        Assert.True(revokeResult.IsValid);

        var secondStarter = os.Get<IFlowStarter>(new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-explicit-revoked"));
        await secondStarter.StartFromJson("ExplicitToggle Revoked Fallback", "owner", null, payloadJson);
        await os.Idle();

        var secondHead = await secondStarter.Head();
        Assert.Equal(FlowStatus.Completed, secondHead.Status);
        var secondInner = JsonSerializer.Deserialize<CodeOrchestrationInner>(secondHead.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.Equal("local-unit-host", secondInner.ExecutionTarget);
        Assert.Null(secondInner.ExecutedByMachineId);
        Assert.NotNull(secondInner.RouteReason);
        Assert.Equal("Computed 60 inside UnitHost", secondInner.Result!.Summary);
        Assert.Equal(1, transport.ExecuteCount);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class DirectClientTransport(ILocalSandboxExecutor executor) : IConnectedComputeTransport
    {
        public int ExecuteCount { get; private set; }

        public Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
            => Task.FromResult(node.IsEligibleForSovereignCompute);

        public async Task<ConnectedSandboxResponse?> ExecuteAsync(
            BrainId brainId,
            ConnectedComputeNode node,
            ConnectedSandboxRequest request,
            CancellationToken ct = default)
        {
            ExecuteCount++;
            var response = await executor.ExecuteAsync(request, ct);
            return response with { ExecutedByMachineId = node.MachineId };
        }
    }

    private sealed class HangingProbeTransport : IConnectedComputeTransport
    {
        public async Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return true;
        }

        public Task<ConnectedSandboxResponse?> ExecuteAsync(
            BrainId brainId,
            ConnectedComputeNode node,
            ConnectedSandboxRequest request,
            CancellationToken ct = default)
            => Task.FromResult<ConnectedSandboxResponse?>(null);
    }

    private sealed class MockCloudSandboxHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            var resultInner = """{"status":"succeeded","summary":"Executed on cloud sandbox fallback.","artifacts":[],"metrics":{"cloud":1}}""";
            var responseJson = path switch
            {
                "/apps/deploy" => """{"brainId":"orch-cloud-fallback","appKey":"orch","version":"1.0.0","digest":"sha256:test","title":"Cloud Fallback","summary":"Test","commands":[{"name":"orchestrate","description":""}],"signals":[],"schedules":[]}""",
                "/apps/dispatch" => JsonSerializer.Serialize(new
                {
                    handled = true,
                    resultJson = JsonSerializer.Serialize(resultInner),
                }),
                "/apps/unload" => """{"unloaded":true}""",
                _ => "{}",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            });
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class HashSetNonceCache : INonceCache
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public bool TryAdd(string nonce, DateTimeOffset expiresAt) => _seen.Add(nonce);
    }
}
