using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class ConnectedComputeRouterFacts
{
    private static readonly BrainId OwnerBrain = new("owner");
    private static readonly BrainId OtherBrain = new("other-brain");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------
    // Router facts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineAutoAllSandboxReadyClientIsSelectedForSovereignCompute()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var transport = new ScriptedTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            execute: static (_, node, req, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(true, """{"status":"succeeded","summary":"local"}""", null, null, node.MachineId)));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(
            OwnerBrain,
            NewAdvertisement("owner-desktop-1", "Vlad Desktop", ComputeConsentMode.AutoAll, sandboxReady: true, clock.GetUtcNow()),
            Ct);

        var decision = await router.ResolveRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.ConnectedClient, decision.Target);
        Assert.NotNull(decision.Node);
        Assert.Equal("owner-desktop-1", decision.Node!.MachineId);
        Assert.True(decision.Node.IsEligibleForSovereignCompute);

        var response = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-1", OwnerBrain.Value), Ct);
        Assert.NotNull(response);
        Assert.True(response!.Handled);
        Assert.Equal("owner-desktop-1", response.ExecutedByMachineId);
        Assert.Equal(1, transport.ProbeCalls);
        Assert.Equal(1, transport.ExecuteCalls);
    }

    [Theory]
    [InlineData(ComputeConsentMode.Disabled, true)]
    [InlineData(ComputeConsentMode.ExplicitToggle, true)]
    [InlineData(ComputeConsentMode.AutoAll, false)]
    public async Task NonAutoAllOrUnreadyClientsAreSkippedAndFallBack(ComputeConsentMode consentMode, bool sandboxReady)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var transport = new ScriptedTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            execute: static (_, _, _, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(true, "{}", null, null, "m1")));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(
            OwnerBrain,
            NewAdvertisement("m1", "Restricted PC", consentMode, sandboxReady, clock.GetUtcNow()),
            Ct);

        var decision = await router.ResolveRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, decision.Target);
        Assert.Null(decision.Node);

        var response = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-skip", OwnerBrain.Value), Ct);
        Assert.Null(response);
        Assert.Equal(0, transport.ProbeCalls);
        Assert.Equal(0, transport.ExecuteCalls);
    }

    [Fact]
    public async Task MultiBrainIsolationPreventsCrossBrainRoutingAndRejectsMismatchedRelayBrain()
    {
        using var root = new TempDirectory("intochat-compute-isolation-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await credentials.Write(new LinkSecrets(
            new Uri("https://relay.test/"),
            "cred-iso",
            "access-iso",
            clock.GetUtcNow().AddHours(1),
            "refresh-iso",
            clock.GetUtcNow().AddDays(30),
            key.ExportPkcs8PrivateKey()), Ct);

        // Relay returns nodes claiming to belong to OtherBrain when queried for OwnerBrain.
        using var relay = new FakeComputeRelayHandler
        {
            ComputeNodesResponse = new BrainComputeNodesResponse(
                OtherBrain.Value,
                [NewAdvertisement("foreign-pc", "Foreign PC", ComputeConsentMode.AutoAll, sandboxReady: true, clock.GetUtcNow())]),
        };
        using var http = new HttpClient(relay);
        var cloud = new CloudClient(http, credentials, clock);
        var thisMachine = new MachineId(Path.Combine(root.Path, "machine-id"));
        var router = new ConnectedComputeRouter(cloud, credentials, thisMachine, clock: clock);

        await router.RecordHeartbeatAsync(
            OtherBrain,
            NewAdvertisement("other-pc", "Other Desktop", ComputeConsentMode.AutoAll, sandboxReady: true, clock.GetUtcNow()),
            Ct);

        Assert.Empty(await router.ListNodesAsync(OwnerBrain, Ct));
        var decision = await router.ResolveRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, decision.Target);
        Assert.Null(decision.Node);
    }

    [Fact]
    public async Task AuthenticatedHeartbeatRequiresValidP256MachineProofAndKeyThumbprintBinding()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var router = new ConnectedComputeRouter(clock: clock);
        var nonces = new HashSetNonceCache();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicSpki = deviceKey.ExportSubjectPublicKeyInfo();
        var thumbprint = DeviceKeys.Thumbprint(publicSpki);
        var url = new Uri("https://relay.test" + DeviceRoutes.ComputeCapability);
        const string accessToken = "cred-p256.access";

        // 1. Valid P-256 MachineProof + matching key thumbprint succeeds.
        var validProof = MachineProof.Sign(deviceKey, "PUT", url, accessToken, clock.GetUtcNow());
        var adv = NewAdvertisement("desktop-p256", "Signed Desktop", ComputeConsentMode.AutoAll, sandboxReady: true, clock.GetUtcNow(), thumbprint);
        var accepted = await router.RecordAuthenticatedHeartbeatAsync(OwnerBrain, publicSpki, validProof, "PUT", url, accessToken, nonces, adv, Ct);
        Assert.True(accepted.IsValid);
        var recorded = Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct));
        Assert.Equal(thumbprint, recorded.KeyThumbprint);
        Assert.Equal(ComputeNodeStatus.Online, recorded.Status);

        // 2. Replaying the same proof nonce is rejected.
        var replay = await router.RecordAuthenticatedHeartbeatAsync(OwnerBrain, publicSpki, validProof, "PUT", url, accessToken, nonces, adv, Ct);
        Assert.Equal(ProofError.Replay, replay.Error);

        // 3. Proof signed with a different key is rejected.
        using var rogueKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forgedProof = MachineProof.Sign(rogueKey, "PUT", url, accessToken, clock.GetUtcNow());
        var forged = await router.RecordAuthenticatedHeartbeatAsync(OwnerBrain, publicSpki, forgedProof, "PUT", url, accessToken, nonces, adv, Ct);
        Assert.Equal(ProofError.Signature, forged.Error);

        // 4. Valid proof but mismatched claimed thumbprint is rejected.
        var freshProof = MachineProof.Sign(deviceKey, "PUT", url, accessToken, clock.GetUtcNow());
        var wrongThumbAdv = adv with { MachineId = "spoofed-pc", KeyThumbprint = "wrong-thumbprint" };
        var bindingFailed = await router.RecordAuthenticatedHeartbeatAsync(OwnerBrain, publicSpki, freshProof, "PUT", url, accessToken, nonces, wrongThumbAdv, Ct);
        Assert.Equal(ProofError.Binding, bindingFailed.Error);
        Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct));
    }

    [Fact]
    public async Task RouterSelectsMostRecentOnlineAutoAllNodeAndFailsOverToSecondHealthyNode()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var probed = new List<string>();
        var transport = new ScriptedTransport(
            probe: (_, node, _) =>
            {
                probed.Add(node.MachineId);
                return Task.FromResult(node.MachineId == "older-healthy");
            },
            execute: static (_, node, _, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(true, """{"status":"succeeded"}""", null, null, node.MachineId)));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("older-healthy", "Laptop", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("newer-stale", "Desktop", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);

        // Most recent heartbeat is preferred first; when its probe fails it is marked offline and router fails over.
        var response = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-failover", OwnerBrain.Value), Ct);
        Assert.NotNull(response);
        Assert.Equal("older-healthy", response!.ExecutedByMachineId);
        Assert.Equal(["newer-stale", "older-healthy"], probed);

        var nodes = await router.ListNodesAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeNodeStatus.Offline, nodes.Single(n => n.MachineId == "newer-stale").Status);
        Assert.Equal(ComputeNodeStatus.Online, nodes.Single(n => n.MachineId == "older-healthy").Status);
    }

    // -------------------------------------------------------------------------
    // Heartbeat facts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HeartbeatTransitionsNodeToOfflineAfterTtlExpiryAndRestoresOnFreshHeartbeat()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions { HeartbeatTtlSeconds = 30 },
        });
        var router = new ConnectedComputeRouter(options: options, clock: clock);

        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("pc-hb", "Main PC", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);
        Assert.Equal(ComputeNodeStatus.Online, Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct)).Status);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ComputeNodeStatus.Online, Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct)).Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        var expired = Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct));
        Assert.Equal(ComputeNodeStatus.Offline, expired.Status);
        Assert.False(expired.IsEligibleForSovereignCompute);
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, (await router.ResolveRouteAsync(OwnerBrain, Ct)).Target);

        // Fresh heartbeat restores Online status immediately.
        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("pc-hb", "Main PC", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);
        Assert.Equal(ComputeNodeStatus.Online, Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct)).Status);
        Assert.Equal(ComputeExecutionTarget.ConnectedClient, (await router.ResolveRouteAsync(OwnerBrain, Ct)).Target);
    }

    [Fact]
    public async Task MarkOfflineImmediatelyTransitionsNodeToOffline()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var router = new ConnectedComputeRouter(clock: clock);

        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("pc-drop", "Drop PC", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);
        Assert.Equal(ComputeNodeStatus.Online, Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct)).Status);

        await router.MarkOfflineAsync(OwnerBrain, "pc-drop", Ct);
        var node = Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct));
        Assert.Equal(ComputeNodeStatus.Offline, node.Status);
        Assert.False(node.IsEligibleForSovereignCompute);
    }

    [Fact]
    public async Task DeviceWorkerAdvertisesAutoAllComputeCapabilityOverP256SignedChannelAndRefreshesHeartbeat()
    {
        using var root = new TempDirectory("intochat-worker-cap-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var machine = new MachineId(Path.Combine(root.Path, "machine-id"));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicSpki = key.ExportSubjectPublicKeyInfo();
        var expectedThumbprint = DeviceKeys.Thumbprint(publicSpki);
        const string accessToken = "cred-worker.access";

        await credentials.Write(new LinkSecrets(
            new Uri("https://relay.test"),
            "cred-worker",
            accessToken,
            clock.GetUtcNow().AddHours(1),
            "cred-worker.refresh",
            clock.GetUtcNow().AddDays(30),
            key.ExportPkcs8PrivateKey()), Ct);

        using var relay = new FakeComputeRelayHandler();
        using var http = new HttpClient(relay);
        var cloud = new CloudClient(http, credentials, clock);
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions
            {
                ConsentMode = ComputeConsentMode.AutoAll,
                SandboxEnabled = true,
                HeartbeatIntervalSeconds = 15,
            },
        });
        var router = new ConnectedComputeRouter(clock: clock);
        using var worker = new DeviceWorker(
            cloud,
            credentials,
            [],
            machine,
            NullLogger<DeviceWorker>.Instance,
            clock,
            () => "owner-workstation",
            services: null,
            options: options,
            computeRouter: router);

        await worker.Tick(Ct);

        // 1. First tick advertises AutoAll + SandboxReady over P-256 signed PUT /circle/v1/compute/capability.
        var capReq = Assert.Single(relay.CapabilityRequests);
        Assert.Equal(accessToken, capReq.Bearer);
        var verify = MachineProof.Verify(
            publicSpki,
            capReq.Proof,
            "PUT",
            capReq.Url,
            accessToken,
            clock.GetUtcNow(),
            new HashSetNonceCache());
        Assert.True(verify.IsValid);
        Assert.Equal(machine.Value, capReq.Advertisement.MachineId);
        Assert.Equal("owner-workstation", capReq.Advertisement.DeviceName);
        Assert.Equal(ComputeConsentMode.AutoAll, capReq.Advertisement.ConsentMode);
        Assert.True(capReq.Advertisement.SandboxReady);
        Assert.Equal("unit-host", capReq.Advertisement.SandboxRuntime);
        Assert.Equal(expectedThumbprint, capReq.Advertisement.KeyThumbprint);

        // Local router also records the heartbeat.
        var localNode = Assert.Single(await router.ListNodesAsync(new BrainId(IntoChatOSNames.DefaultOwnerBrainId), Ct));
        Assert.Equal(ComputeNodeStatus.Online, localNode.Status);
        Assert.True(localNode.IsEligibleForSovereignCompute);

        // 2. Tick before heartbeat interval does not spam duplicate capability PUTs.
        clock.Advance(TimeSpan.FromSeconds(10));
        await worker.Tick(Ct);
        Assert.Single(relay.CapabilityRequests);

        // 3. Tick at heartbeat interval sends refreshed P-256 heartbeat.
        clock.Advance(TimeSpan.FromSeconds(5));
        await worker.Tick(Ct);
        Assert.Equal(2, relay.CapabilityRequests.Count);
    }

    [Fact]
    public async Task DeviceWorkerWithDisabledConsentDoesNotAdvertiseSandboxReadyAndRefusesSandboxJobs()
    {
        using var root = new TempDirectory("intochat-worker-disabled-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var machine = new MachineId(Path.Combine(root.Path, "machine-id"));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await credentials.Write(new LinkSecrets(
            new Uri("https://relay.test"),
            "cred-dis",
            "cred-dis.access",
            clock.GetUtcNow().AddHours(1),
            "cred-dis.refresh",
            clock.GetUtcNow().AddDays(30),
            key.ExportPkcs8PrivateKey()), Ct);

        using var relay = new FakeComputeRelayHandler();
        relay.PendingSandboxJobs.Enqueue(NewSandboxRequest("job-denied", OwnerBrain.Value));
        using var http = new HttpClient(relay);
        var cloud = new CloudClient(http, credentials, clock);
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions { ConsentMode = ComputeConsentMode.Disabled },
        });
        var executor = new RecordingLocalSandboxExecutor();
        using var worker = new DeviceWorker(
            cloud,
            credentials,
            [],
            machine,
            NullLogger<DeviceWorker>.Instance,
            clock,
            () => "disabled-pc",
            services: null,
            options: options,
            computeRouter: null,
            localSandbox: executor);

        await worker.Tick(Ct);

        var cap = Assert.Single(relay.CapabilityRequests).Advertisement;
        Assert.Equal(ComputeConsentMode.Disabled, cap.ConsentMode);
        Assert.False(cap.SandboxReady);
        Assert.Empty(executor.Executed);

        var reported = Assert.Single(relay.SandboxResults);
        Assert.Equal("job-denied", reported.Key);
        Assert.False(reported.Value.Handled);
        Assert.Equal("consent-disabled", reported.Value.Error);
    }

    [Fact]
    public async Task DeviceWorkerPollsAndExecutesScopedSandboxJobOnAttachedLocalSandboxExecutor()
    {
        using var root = new TempDirectory("intochat-worker-exec-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var machine = new MachineId(Path.Combine(root.Path, "machine-id"));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await credentials.Write(new LinkSecrets(
            new Uri("https://relay.test"),
            "cred-exec",
            "cred-exec.access",
            clock.GetUtcNow().AddHours(1),
            "cred-exec.refresh",
            clock.GetUtcNow().AddDays(30),
            key.ExportPkcs8PrivateKey()), Ct);

        using var relay = new FakeComputeRelayHandler();
        relay.PendingSandboxJobs.Enqueue(NewSandboxRequest("job-run-1", OwnerBrain.Value));
        using var http = new HttpClient(relay);
        var cloud = new CloudClient(http, credentials, clock);
        var executor = new RecordingLocalSandboxExecutor();
        using var worker = new DeviceWorker(
            cloud,
            credentials,
            [],
            machine,
            NullLogger<DeviceWorker>.Instance,
            clock,
            () => "autoall-pc",
            services: null,
            options: null,
            computeRouter: null,
            localSandbox: executor);

        await worker.Tick(Ct);

        Assert.Single(executor.Executed);
        var reported = Assert.Single(relay.SandboxResults);
        Assert.Equal("job-run-1", reported.Key);
        Assert.True(reported.Value.Handled);
        Assert.Equal(machine.Value, reported.Value.ExecutedByMachineId);
    }

    // -------------------------------------------------------------------------
    // Fallback facts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SlowOrHangingConnectedClientProbeTimesOutUnder500MsMarksNodeOfflineAndReturnsNullForFallback()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions { AvailabilityTimeoutMs = 200 },
        });
        var transport = new ScriptedTransport(
            probe: static async (_, _, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return true;
            },
            execute: static (_, _, _, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(true, "{}", null, null, "hanging-pc")));
        var router = new ConnectedComputeRouter(options: options, clock: clock, transport: transport);

        Assert.True(router.AvailabilityTimeout < TimeSpan.FromMilliseconds(500));
        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("hanging-pc", "Sleeping Laptop", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);

        var sw = Stopwatch.StartNew();
        var result = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-timeout", OwnerBrain.Value), Ct);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500), $"Expected fast <500ms timeout detection, took {sw.ElapsedMilliseconds}ms.");
        Assert.Equal(0, transport.ExecuteCalls);

        // Node is immediately marked Offline after timing out.
        var node = Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct));
        Assert.Equal(ComputeNodeStatus.Offline, node.Status);
    }

    [Fact]
    public async Task MidDispatchDisconnectMarksNodeOfflineAndReturnsNullForTransparentFallback()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var transport = new ScriptedTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            execute: static (_, _, _, _) => throw new DeviceUnavailableException("device-disconnected"));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("drop-mid-job", "Flaky Laptop", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);

        var result = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-mid", OwnerBrain.Value), Ct);

        Assert.Null(result);
        var node = Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct));
        Assert.Equal(ComputeNodeStatus.Offline, node.Status);
    }

    [Fact]
    public async Task PolicyOrScopedTokenErrorFromHealthyClientIsReturnedDirectlyWithoutFallbackMasking()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var transport = new ScriptedTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            execute: static (_, node, _, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(
                    Handled: false,
                    ResultJson: null,
                    UpdatedStateJson: null,
                    Error: "Contract 'IntoChat.Sdk.INotifications' is outside the scoped AppToken.",
                    ExecutedByMachineId: node.MachineId)));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(OwnerBrain, NewAdvertisement("healthy-pc", "Healthy PC", ComputeConsentMode.AutoAll, true, clock.GetUtcNow()), Ct);

        var result = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-policy", OwnerBrain.Value), Ct);

        Assert.NotNull(result);
        Assert.False(result!.Handled);
        Assert.Contains("outside the scoped AppToken", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("healthy-pc", result.ExecutedByMachineId);
        // Healthy client stays Online.
        Assert.Equal(ComputeNodeStatus.Online, Assert.Single(await router.ListNodesAsync(OwnerBrain, Ct)).Status);
    }

    [Fact]
    public async Task HttpSandboxRuntimeBridgeFallsBackTransparentlyToCloudSandboxWhenRouterReturnsNull()
    {
        var cloudHandler = new FakeCloudSandboxWorkerHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("SandboxWorker").ConfigurePrimaryHttpMessageHandler(() => cloudHandler);
        var sandboxOptions = Options.Create(new SandboxOptions
        {
            Url = "https://sandbox-cloud.test",
            TokenSigningMaterial = "unit-test-cloud-fallback-secret",
            TokenTtlMinutes = 15,
        });
        services.AddSingleton(sandboxOptions);
        services.AddSingleton<AppTokenService>();
        await using var provider = services.BuildServiceProvider();

        // Router with no online nodes -> TryExecuteOnConnectedClientAsync returns null.
        var emptyRouter = new ConnectedComputeRouter();
        await using var bridge = new HttpSandboxRuntimeBridge(
            provider.GetRequiredService<IHttpClientFactory>(),
            sandboxOptions,
            provider.GetRequiredService<AppTokenService>(),
            provider,
            NullLogger<HttpSandboxRuntimeBridge>.Instance,
            emptyRouter);

        var package = new AppPackage(
            Key: "orch-cloud-fallback",
            Version: "1.0.0",
            Title: "Fallback Test",
            Summary: "Test",
            Icon: "code",
            Category: "orchestration",
            Author: "test",
            SourceFiles: [new AppSourceFile("Program.cs", "// test")],
            Needs: AppPackageNeeds.Empty,
            DefaultSettingsJson: "{}",
            Digest: "sha256:test");

        var dispatch = await bridge.ExecuteScopedCommandAsync(
            OwnerBrain,
            package,
            assemblyBytes: [1, 2, 3, 4],
            command: "orchestrate",
            payloadJson: """{"count":1}""",
            allowedContracts: ["notifications"],
            cancellationToken: Ct);

        Assert.True(dispatch.Handled);
        Assert.Equal("cloud-sandbox", dispatch.ExecutionTarget);
        Assert.Null(dispatch.ExecutedByMachineId);
        Assert.Equal(["/apps/deploy", "/apps/dispatch", "/apps/unload"], cloudHandler.Paths);
    }

    // -------------------------------------------------------------------------
    // RFC-2 Explicit Device Toggle & ICodeSandbox capability facts
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Microsoft Windows 10.0.26100", ClientDevicePlatform.Windows, true)]
    [InlineData("Windows 11 Pro", ClientDevicePlatform.Windows, true)]
    [InlineData("Darwin 24.1.0 Darwin Kernel Version 24.1.0", ClientDevicePlatform.MacOS, true)]
    [InlineData("macOS 15.1", ClientDevicePlatform.MacOS, true)]
    [InlineData("Ubuntu 24.04.1 LTS (Linux 6.8.0)", ClientDevicePlatform.Linux, true)]
    [InlineData("Linux 6.1.75-android14-11-g1234", ClientDevicePlatform.Android, false)]
    [InlineData("Android 15", ClientDevicePlatform.Android, false)]
    [InlineData("iOS 18.2", ClientDevicePlatform.IOS, false)]
    [InlineData("iPhone OS 18.1", ClientDevicePlatform.IOS, false)]
    [InlineData("iPadOS 18.0", ClientDevicePlatform.IOS, false)]
    [InlineData("", ClientDevicePlatform.Unknown, false)]
    [InlineData(null, ClientDevicePlatform.Unknown, false)]
    public void PlatformConsentPolicyDefaultsTrueOnDesktopAndFalseOnMobileOrUnknown(
        string? osDescription,
        ClientDevicePlatform expectedPlatform,
        bool expectedDefaultConsent)
    {
        var classified = ClientComputeConsentPolicy.ClassifyPlatform(osDescription);
        Assert.Equal(expectedPlatform, classified);
        Assert.Equal(expectedDefaultConsent, ClientComputeConsentPolicy.DefaultAllowClientCodeSandbox(classified));
        Assert.Equal(expectedDefaultConsent, ClientComputeConsentPolicy.DefaultAllowClientCodeSandbox(osDescription));
    }

    [Fact]
    public void ComputeCapabilityOptionsDefaultsToExplicitToggleWithCurrentPlatformConsent()
    {
        var defaults = new ComputeCapabilityOptions();
        Assert.Equal(ComputeConsentMode.ExplicitToggle, defaults.ConsentMode);
        Assert.Equal(
            ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform(),
            defaults.AllowClientCodeSandbox);
    }

    [Fact]
    public async Task ExplicitToggleWithAllowClientCodeSandboxAndICodeSandboxCapabilityIsEligibleAndSelected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var transport = new ScriptedTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            execute: static (_, node, _, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(true, """{"status":"succeeded","summary":"explicit-toggle"}""", null, null, node.MachineId)));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(
            OwnerBrain,
            NewAdvertisement(
                "desktop-explicit-1",
                "Owner MacBook",
                ComputeConsentMode.ExplicitToggle,
                sandboxReady: true,
                clock.GetUtcNow(),
                allowClientCodeSandbox: true,
                advertisedCapabilities: [ICodeSandbox.CapabilityId]),
            Ct);

        var decision = await router.ResolveRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.ConnectedClient, decision.Target);
        Assert.NotNull(decision.Node);
        Assert.Equal("desktop-explicit-1", decision.Node!.MachineId);
        Assert.True(decision.Node.AllowClientCodeSandbox);
        Assert.True(decision.Node.AdvertisesCodeSandbox);
        Assert.True(decision.Node.IsEligibleForSovereignCompute);

        var response = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-explicit-1", OwnerBrain.Value), Ct);
        Assert.NotNull(response);
        Assert.True(response!.Handled);
        Assert.Equal("desktop-explicit-1", response.ExecutedByMachineId);
        Assert.Equal(1, transport.ProbeCalls);
        Assert.Equal(1, transport.ExecuteCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ExplicitToggleRequiresBothConsentFlagAndICodeSandboxCapability(
        bool allowClientCodeSandbox,
        bool advertiseCodeSandbox)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var transport = new ScriptedTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            execute: static (_, node, _, _) => Task.FromResult<ConnectedSandboxResponse?>(
                new ConnectedSandboxResponse(true, "{}", null, null, node.MachineId)));
        var router = new ConnectedComputeRouter(clock: clock, transport: transport);

        await router.RecordHeartbeatAsync(
            OwnerBrain,
            NewAdvertisement(
                "mobile-or-unready",
                "Owner Phone",
                ComputeConsentMode.ExplicitToggle,
                sandboxReady: true,
                clock.GetUtcNow(),
                allowClientCodeSandbox: allowClientCodeSandbox,
                advertisedCapabilities: advertiseCodeSandbox ? [ICodeSandbox.CapabilityId] : []),
            Ct);

        var decision = await router.ResolveRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, decision.Target);
        Assert.Null(decision.Node);

        var response = await router.TryExecuteOnConnectedClientAsync(OwnerBrain, NewSandboxRequest("job-skip-explicit", OwnerBrain.Value), Ct);
        Assert.Null(response);
        Assert.Equal(0, transport.ProbeCalls);
        Assert.Equal(0, transport.ExecuteCalls);
    }

    [Fact]
    public async Task RelayTransportProbeEnforcesExplicitToggleAndICodeSandboxCapability()
    {
        using var root = new TempDirectory("intochat-relay-probe-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await credentials.Write(new LinkSecrets(
            new Uri("https://relay.test/"),
            "cred-probe",
            "access-probe",
            clock.GetUtcNow().AddHours(1),
            "refresh-probe",
            clock.GetUtcNow().AddDays(30),
            key.ExportPkcs8PrivateKey()), Ct);

        using var relay = new FakeComputeRelayHandler
        {
            ComputeNodesResponse = new BrainComputeNodesResponse(
                OwnerBrain.Value,
                [
                    NewAdvertisement(
                        "desktop-ok",
                        "Desktop OK",
                        ComputeConsentMode.ExplicitToggle,
                        sandboxReady: true,
                        clock.GetUtcNow(),
                        allowClientCodeSandbox: true,
                        advertisedCapabilities: [ICodeSandbox.CapabilityId]),
                    NewAdvertisement(
                        "mobile-denied",
                        "Mobile Denied",
                        ComputeConsentMode.ExplicitToggle,
                        sandboxReady: true,
                        clock.GetUtcNow(),
                        allowClientCodeSandbox: false,
                        advertisedCapabilities: [ICodeSandbox.CapabilityId]),
                ]),
        };
        using var http = new HttpClient(relay);
        var cloud = new CloudClient(http, credentials, clock);
        var transport = new RelayConnectedComputeTransport(cloud, credentials);

        var okNode = new ConnectedComputeNode(
            BrainId: OwnerBrain.Value,
            MachineId: "desktop-ok",
            DeviceName: "Desktop OK",
            ConsentMode: ComputeConsentMode.ExplicitToggle,
            SandboxReady: true,
            SandboxRuntime: "unit-host",
            Status: ComputeNodeStatus.Online,
            LastHeartbeatAt: clock.GetUtcNow(),
            KeyThumbprint: null,
            AllowClientCodeSandbox: true,
            AdvertisedCapabilities: [ICodeSandbox.CapabilityId]);
        var deniedNode = okNode with { MachineId = "mobile-denied", AllowClientCodeSandbox = false };

        Assert.True(await transport.ProbeAsync(OwnerBrain, okNode, Ct));
        Assert.False(await transport.ProbeAsync(OwnerBrain, deniedNode, Ct));
    }

    [Fact]
    public async Task DeviceWorkerAdvertisesICodeSandboxOverP256AndRevokesImmediatelyWhenToggleDisabled()
    {
        using var root = new TempDirectory("intochat-worker-toggle-");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var machine = new MachineId(Path.Combine(root.Path, "machine-id"));
        var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicSpki = key.ExportSubjectPublicKeyInfo();
        const string accessToken = "cred-toggle.access";

        await credentials.Write(new LinkSecrets(
            new Uri("https://relay.test"),
            "cred-toggle",
            accessToken,
            clock.GetUtcNow().AddHours(1),
            "cred-toggle.refresh",
            clock.GetUtcNow().AddDays(30),
            key.ExportPkcs8PrivateKey()), Ct);

        using var relay = new FakeComputeRelayHandler();
        using var http = new HttpClient(relay);
        var cloud = new CloudClient(http, credentials, clock);
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions
            {
                ConsentMode = ComputeConsentMode.ExplicitToggle,
                AllowClientCodeSandbox = true,
                SandboxEnabled = true,
                HeartbeatIntervalSeconds = 60,
            },
        });
        var consentStore = new ClientCodeSandboxConsentStore(true);
        var router = new ConnectedComputeRouter(clock: clock);
        var executor = new RecordingLocalSandboxExecutor();
        using var worker = new DeviceWorker(
            cloud,
            credentials,
            [],
            machine,
            NullLogger<DeviceWorker>.Instance,
            clock,
            () => "owner-desktop",
            services: null,
            options: options,
            computeRouter: router,
            localSandbox: executor,
            consentStore: consentStore);

        // 1. Enabled toggle: first tick advertises ICodeSandbox over P-256 and executes queued job.
        relay.PendingSandboxJobs.Enqueue(NewSandboxRequest("job-enabled", OwnerBrain.Value));
        await worker.Tick(Ct);

        var firstCap = Assert.Single(relay.CapabilityRequests);
        Assert.True(MachineProof.Verify(publicSpki, firstCap.Proof, "PUT", firstCap.Url, accessToken, clock.GetUtcNow(), new HashSetNonceCache()).IsValid);
        Assert.Equal(ComputeConsentMode.ExplicitToggle, firstCap.Advertisement.ConsentMode);
        Assert.True(firstCap.Advertisement.AllowClientCodeSandbox);
        Assert.True(firstCap.Advertisement.SandboxReady);
        Assert.Contains(ICodeSandbox.CapabilityId, firstCap.Advertisement.AdvertisedCapabilities ?? []);

        var ownerBrainId = new BrainId(IntoChatOSNames.DefaultOwnerBrainId);
        var enabledNode = Assert.Single(await router.ListNodesAsync(ownerBrainId, Ct));
        Assert.Equal(ComputeNodeStatus.Online, enabledNode.Status);
        Assert.True(enabledNode.IsEligibleForSovereignCompute);
        Assert.True(relay.SandboxResults["job-enabled"].Handled);
        Assert.Single(executor.Executed);

        // 2. Disable toggle: next tick immediately sends P-256 revocation (before heartbeat interval),
        // marks node Offline/Ineligible in router, and refuses incoming sandbox jobs.
        consentStore.Set(false);
        clock.Advance(TimeSpan.FromSeconds(1));
        relay.PendingSandboxJobs.Enqueue(NewSandboxRequest("job-revoked", OwnerBrain.Value));
        await worker.Tick(Ct);

        Assert.Equal(2, relay.CapabilityRequests.Count);
        var revokedCap = relay.CapabilityRequests[1];
        Assert.True(MachineProof.Verify(publicSpki, revokedCap.Proof, "PUT", revokedCap.Url, accessToken, clock.GetUtcNow(), new HashSetNonceCache()).IsValid);
        Assert.Equal(ComputeConsentMode.ExplicitToggle, revokedCap.Advertisement.ConsentMode);
        Assert.False(revokedCap.Advertisement.AllowClientCodeSandbox);
        Assert.False(revokedCap.Advertisement.SandboxReady);
        Assert.Empty(revokedCap.Advertisement.AdvertisedCapabilities ?? []);

        var revokedNode = Assert.Single(await router.ListNodesAsync(ownerBrainId, Ct));
        Assert.Equal(ComputeNodeStatus.Offline, revokedNode.Status);
        Assert.False(revokedNode.IsEligibleForSovereignCompute);
        Assert.False(relay.SandboxResults["job-revoked"].Handled);
        Assert.Equal("consent-disabled", relay.SandboxResults["job-revoked"].Error);
        Assert.Single(executor.Executed);

        // 3. Re-enable toggle: next tick immediately re-advertises ICodeSandbox and restores Online/Eligible.
        consentStore.Set(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await worker.Tick(Ct);

        Assert.Equal(3, relay.CapabilityRequests.Count);
        var restoredCap = relay.CapabilityRequests[2];
        Assert.True(restoredCap.Advertisement.AllowClientCodeSandbox);
        Assert.True(restoredCap.Advertisement.SandboxReady);
        Assert.Contains(ICodeSandbox.CapabilityId, restoredCap.Advertisement.AdvertisedCapabilities ?? []);

        var restoredNode = Assert.Single(await router.ListNodesAsync(ownerBrainId, Ct));
        Assert.Equal(ComputeNodeStatus.Online, restoredNode.Status);
        Assert.True(restoredNode.IsEligibleForSovereignCompute);
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private static ComputeCapabilityAdvertisement NewAdvertisement(
        string machineId,
        string deviceName,
        ComputeConsentMode consentMode,
        bool sandboxReady,
        DateTimeOffset heartbeatAt,
        string? thumbprint = null,
        bool allowClientCodeSandbox = false,
        string[]? advertisedCapabilities = null)
        => new(
            MachineId: machineId,
            DeviceName: deviceName,
            ConsentMode: consentMode,
            SandboxReady: sandboxReady,
            SandboxRuntime: "unit-host",
            OsDescription: "Windows 11 Test",
            MaxConcurrency: 2,
            HeartbeatAt: heartbeatAt,
            KeyThumbprint: thumbprint,
            AllowClientCodeSandbox: allowClientCodeSandbox,
            AdvertisedCapabilities: advertisedCapabilities);

    private static ConnectedSandboxRequest NewSandboxRequest(string jobId, string brainId)
        => new(
            JobId: jobId,
            BrainId: brainId,
            AppKey: "orch-test",
            Version: "1.0.0",
            Digest: "sha256:abc",
            AssemblyBase64: Convert.ToBase64String([1, 2, 3]),
            Command: "orchestrate",
            PayloadJson: """{"x":1}""",
            StateJson: null,
            SettingsJson: null,
            KernelCallbackUrl: "https://kernel.test",
            AppToken: "scoped-token-placeholder",
            AllowedContracts: ["notifications"]);

    private sealed class ScriptedTransport(
        Func<BrainId, ConnectedComputeNode, CancellationToken, Task<bool>> probe,
        Func<BrainId, ConnectedComputeNode, ConnectedSandboxRequest, CancellationToken, Task<ConnectedSandboxResponse?>> execute) : IConnectedComputeTransport
    {
        public int ProbeCalls { get; private set; }
        public int ExecuteCalls { get; private set; }

        public Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
        {
            ProbeCalls++;
            return probe(brainId, node, ct);
        }

        public Task<ConnectedSandboxResponse?> ExecuteAsync(BrainId brainId, ConnectedComputeNode node, ConnectedSandboxRequest request, CancellationToken ct = default)
        {
            ExecuteCalls++;
            return execute(brainId, node, request, ct);
        }
    }

    private sealed class RecordingLocalSandboxExecutor : ILocalSandboxExecutor
    {
        public List<ConnectedSandboxRequest> Executed { get; } = [];

        public Task<ConnectedSandboxResponse> ExecuteAsync(ConnectedSandboxRequest request, CancellationToken ct = default)
        {
            Executed.Add(request);
            return Task.FromResult(new ConnectedSandboxResponse(
                Handled: true,
                ResultJson: """{"status":"succeeded","summary":"local-unit-host"}""",
                UpdatedStateJson: null,
                Error: null));
        }
    }

    private sealed record RecordedCapabilityRequest(Uri Url, string? Bearer, string Proof, ComputeCapabilityAdvertisement Advertisement);

    private sealed class FakeComputeRelayHandler : HttpMessageHandler
    {
        public List<RecordedCapabilityRequest> CapabilityRequests { get; } = [];
        public Queue<ConnectedSandboxRequest> PendingSandboxJobs { get; } = new();
        public Dictionary<string, ConnectedSandboxResponse> SandboxResults { get; } = new(StringComparer.Ordinal);
        public BrainComputeNodesResponse? ComputeNodesResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var bearer = request.Headers.Authorization?.Parameter;
            var proof = request.Headers.TryGetValues(CircleRoutes.MachineProofHeader, out var values) ? values.Single() : string.Empty;

            if (path == DeviceRoutes.Models && request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == DeviceRoutes.ComputeCapability && request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var adv = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ComputeCapabilityAdvertisement)!;
                CapabilityRequests.Add(new RecordedCapabilityRequest(request.RequestUri, bearer, proof, adv));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == DeviceRoutes.ComputeNodes && request.Method == HttpMethod.Get)
            {
                var payload = ComputeNodesResponse ?? new BrainComputeNodesResponse(OwnerBrain.Value, []);
                var json = JsonSerializer.Serialize(payload, DeviceWireJson.Default.BrainComputeNodesResponse);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
            }

            if (path == DeviceRoutes.SandboxNext && request.Method == HttpMethod.Get)
            {
                if (PendingSandboxJobs.Count == 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                var next = PendingSandboxJobs.Dequeue();
                var json = JsonSerializer.Serialize(next, DeviceWireJson.Default.ConnectedSandboxRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                };
            }

            if (path.StartsWith(CircleRoutes.Prefix + "/compute/sandbox/", StringComparison.Ordinal)
                && path.EndsWith("/result", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var prefix = CircleRoutes.Prefix + "/compute/sandbox/";
                var jobId = Uri.UnescapeDataString(path[prefix.Length..^"/result".Length]);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ConnectedSandboxResponse)!;
                SandboxResults[jobId] = result;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == DeviceRoutes.Next && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class FakeCloudSandboxWorkerHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            var responseJson = path switch
            {
                "/apps/deploy" => """{"brainId":"owner","appKey":"orch-cloud-fallback","version":"1.0.0","digest":"sha256:test","title":"Fallback Test","summary":"Test","commands":[{"name":"orchestrate","description":""}],"signals":[],"schedules":[]}""",
                "/apps/dispatch" => """{"handled":true,"resultJson":"{\"status\":\"succeeded\",\"summary\":\"cloud\"}"}""",
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

    private sealed class XorProtector : IMachineKeyProtector
    {
        public byte[] Protect(byte[] key) => [.. key.Select(static b => (byte)(b ^ 0x42))];
        public byte[] Unprotect(byte[] stored) => [.. stored.Select(static b => (byte)(b ^ 0x42))];
    }

    private sealed class HashSetNonceCache : INonceCache
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public bool TryAdd(string nonce, DateTimeOffset expiresAt) => _seen.Add(nonce);
    }
}
