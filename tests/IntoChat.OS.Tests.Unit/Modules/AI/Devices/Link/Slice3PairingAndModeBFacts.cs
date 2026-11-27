using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Cli;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class Slice3PairingAndModeBFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------
    // 1. Option A: intochat://enroll URI validation, Desktop IPC, CLI fallback
    // -------------------------------------------------------------------------

    [Fact]
    public void DeviceEnrollUriAcceptsHttpsAndLoopbackAndRejectsInsecureOrMalformedUris()
    {
        var canonical = Code.New();
        var built = DeviceEnrollUri.Build(new Uri("https://cloud.intochat.io"), canonical);
        Assert.True(DeviceEnrollUri.TryParse(built.AbsoluteUri, out var server, out var parsed, out var error), error);
        Assert.Equal("https://cloud.intochat.io/", server!.AbsoluteUri);
        Assert.Equal(canonical, parsed);

        // 6-char manual fallback code & localhost dev server
        Assert.True(DeviceEnrollUri.TryParse("intochat://enroll?server=http%3A%2F%2Flocalhost%3A5080&code=AB12CD", out var localServer, out var sixChar, out error), error);
        Assert.Equal("http://localhost:5080/", localServer!.AbsoluteUri);
        Assert.Equal("AB12CD", sixChar.Value);

        // Security rejections (including path/query/fragment injection on server origin)
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=http%3A%2F%2Fevil.example&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=https%3A%2F%2Fuser%3Apass%40cloud.intochat.io&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io%2Fevil%2Fpath&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io%3Ffoo%3Dbar&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io%23frag&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("https://enroll?server=https%3A%2F%2Fcloud.intochat.io&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://other?server=https%3A%2F%2Fcloud.intochat.io&code=AB12CD", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io&code=123", out _, out _, out _));
        Assert.False(DeviceEnrollUri.TryParse("intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io&code=BAD!CODE", out _, out _, out _));
    }

    [Fact]
    public async Task DesktopSingleInstanceIpcForwardsEnrollUriAndStagesPendingInsteadOfAutoJoining()
    {
        var home = Path.Combine(Path.GetTempPath(), "intochat-ipc-test-" + Guid.NewGuid().ToString("N")[..8]);
        var layout = DesktopBootstrap.ResolveLayout(home);
        var pipeName = DesktopBootstrap.GetSingleInstancePipeName(layout);

        var validUri = "intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io&code=ABCD-EFGH-JKMN";
        Assert.True(DesktopBootstrap.TryExtractEnrollUri(["--enroll", validUri], out var extracted));
        Assert.Equal(validUri, extracted);
        Assert.True(DesktopBootstrap.TryExtractEnrollUri([validUri], out var positional));
        Assert.Equal(validUri, positional);
        Assert.True(DesktopBootstrap.TryParseEnrollUri(validUri, out var server, out var code, out var err), err);
        Assert.Equal("https://cloud.intochat.io/", server!.AbsoluteUri);
        Assert.Equal("ABCD-EFGH-JKMN", code);
        Assert.False(DesktopBootstrap.TryParseEnrollUri("intochat://enroll?server=http://remote.bad&code=ABCDEF", out _, out _, out _));
        Assert.False(DesktopBootstrap.TryParseEnrollUri("intochat://enroll?server=https%3A%2F%2Fcloud.intochat.io%2Finjected&code=ABCDEF", out _, out _, out _));

        // Verify SendEnrollToKernelAsync posts to /link/pending (anti-drive-by), NOT /link/enroll
        string? postedPath = null;
        using var kernelHandler = new StubHttpHandler(req =>
        {
            postedPath = req.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });
        using var kernelHttp = new HttpClient(kernelHandler);
        var (stagedOk, stagedErr) = await DesktopBootstrap.SendEnrollToKernelAsync(
            kernelHttp,
            new Uri("http://127.0.0.1:5080/"),
            "presence-token",
            validUri,
            Ct);
        Assert.True(stagedOk, stagedErr);
        Assert.Equal("/brains/owner/devices/link/pending", postedPath);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pipeServer = DesktopBootstrap.StartEnrollPipeServer(
            pipeName,
            (uri, _) =>
            {
                received.TrySetResult(uri);
                return Task.FromResult(true);
            },
            Ct);

        await Task.Delay(50, Ct);
        var forwarded = await DesktopBootstrap.TryForwardEnrollUriAsync(pipeName, validUri, TimeSpan.FromSeconds(2), Ct);
        Assert.True(forwarded);
        Assert.Equal(validUri, await received.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct));
    }

    [Fact]
    public async Task CliLinkStatusAndUnlinkCallKernelEndpointsWithoutEchoingRawCode()
    {
        var seen = new List<(HttpMethod Method, string Path, string Body)>();
        using var handler = new StubHttpHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(Ct);
            seen.Add((req.Method, req.RequestUri!.PathAndQuery, body));
            if (req.RequestUri.PathAndQuery.EndsWith("/devices/link/leave", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"state":"Connected","server":"https://cloud.intochat.io","deviceName":"PC-1"}""", Encoding.UTF8, "application/json"),
            };
        });

        var pairingCode = "AAAA-BBBB-CCCC";
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var env = new Func<string, string?>(name => name == CliProof.PresenceVariable ? "local-presence-token" : null);

        var exitLink = await CliCommands.Run(["link", "https://cloud.intochat.io", pairingCode], stdout, stderr, env, handler);
        Assert.Equal(0, exitLink);
        Assert.DoesNotContain(pairingCode, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pairingCode, stderr.ToString(), StringComparison.Ordinal);

        var exitStatus = await CliCommands.Run(["link", "status"], stdout, stderr, env, handler);
        Assert.Equal(0, exitStatus);

        var exitUnlink = await CliCommands.Run(["unlink"], stdout, stderr, env, handler);
        Assert.Equal(0, exitUnlink);

        Assert.Contains(seen, x => x.Method == HttpMethod.Post && x.Path == "/brains/owner/devices/link/enroll");
        Assert.Contains(seen, x => x.Method == HttpMethod.Get && x.Path == "/brains/owner/devices/link/status");
        Assert.Contains(seen, x => x.Method == HttpMethod.Post && x.Path == "/brains/owner/devices/link/leave");
    }

    // -------------------------------------------------------------------------
    // 2. Option A: SettingsApp Desktop & Cloud Connected Devices views/actions
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SettingsAppDesktopAndCloudRenderAndExecutePairingActions()
    {
        var brain = new BrainId("local");
        Uri? launchedBrowserUrl = null;
        FakeDeviceLink.Reset();
        FakeDeviceEnrollment.Reset();
        var router = new ConnectedComputeRouter(clock: new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero)));

        await router.RecordHeartbeatAsync(
            brain,
            new ComputeCapabilityAdvertisement(
                MachineId: "win-pc-1",
                DeviceName: "My Windows PC",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows 11",
                MaxConcurrency: 2,
                HeartbeatAt: new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero),
                AllowClientCodeSandbox: true,
                AdvertisedCapabilities: [ICodeSandbox.CapabilityId, ClientComputeConsentPolicy.WindowsFolderGatewayCapability],
                AllowWindowsFolderGateway: true,
                ConsentedFolderLabels: ["Projects"]),
            Ct);

        // Desktop (local topology)
        var desktopConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                ["IntoChatOS:CloudUrl"] = "https://cloud.intochat.io",
            })
            .Build();
        var pendingStore = new PendingDeviceEnrollmentStore(new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero)));
        var desktopServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(desktopConfig)
            .AddSingleton<IConnectedComputeRouter>(router)
            .AddSingleton<Action<Uri>>(uri => launchedBrowserUrl = uri)
            .AddSingleton<IPendingDeviceEnrollmentStore>(pendingStore)
            .BuildServiceProvider();

        await using var desktopOs = TestOS.Local(o => o.Services = desktopServices)
            .RegisterCatalog()
            .Register<FakeDeviceLink>();

        var desktopInstalled = await desktopOs.Install<SettingsApp>();
        var desktopSettings = desktopInstalled.App;
        using (desktopOs.Edge.Enter(desktopOs.Edge.Person("local", "local", brain)))
        {
            // Stage a pending protocol handoff: must NOT auto-join until explicitly confirmed
            pendingStore.Stage(new Uri("https://cloud.intochat.io"), Code.New());
            await desktopSettings.Open(new SettingsOpen("general"));
            await desktopSettings.Navigate(new SettingsNavigate("devices"));
            Assert.Equal(0, FakeDeviceLink.JoinCalls);

            var desktopViewJson = JsonSerializer.Serialize(desktopSettings.Render());
            Assert.Contains("Connect this PC to IntoChat Cloud", desktopViewJson, StringComparison.Ordinal);
            Assert.Contains("Connect in browser", desktopViewJson, StringComparison.Ordinal);
            Assert.Contains("Enter pairing code", desktopViewJson, StringComparison.Ordinal);
            Assert.Contains("Confirm cloud link", desktopViewJson, StringComparison.Ordinal);
            Assert.Contains("https://cloud.intochat.io", desktopViewJson, StringComparison.Ordinal);
            Assert.Contains("Link this PC", desktopViewJson, StringComparison.Ordinal);

            // Cancel clears pending without joining
            await desktopSettings.CancelPendingEnrollment(new SettingsCancelPendingEnrollment());
            Assert.Equal(0, FakeDeviceLink.JoinCalls);
            Assert.Null(pendingStore.Current);
            Assert.Null(desktopSettings.State.PendingEnrollmentServerOrigin);

            // Re-stage and confirm explicitly -> joins once
            pendingStore.Stage(new Uri("https://cloud.intochat.io"), Code.New());
            await desktopSettings.ConfirmPendingEnrollment(new SettingsConfirmPendingEnrollment());
            Assert.Equal(LinkState.Connected, desktopSettings.State.DeviceLinkState);
            Assert.Equal(1, FakeDeviceLink.JoinCalls);
            Assert.Null(pendingStore.Current);

            await desktopSettings.DisconnectDevice(new SettingsDisconnectDevice());
            Assert.Equal(LinkState.NotJoined, desktopSettings.State.DeviceLinkState);
            Assert.Equal(1, FakeDeviceLink.LeaveCalls);

            await desktopSettings.LaunchBrowserPairing(new SettingsLaunchBrowserPairing());
            Assert.Equal(new Uri("https://cloud.intochat.io/account/link-device"), launchedBrowserUrl);

            await desktopSettings.PairDevice(new SettingsPairDevice("https://cloud.intochat.io", "ABCD-EFGH-JKMN"));
            Assert.Equal(LinkState.Connected, desktopSettings.State.DeviceLinkState);
            Assert.Equal(2, FakeDeviceLink.JoinCalls);
        }

        // Cloud (cloud topology)
        var cloudConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyCloud,
                ["IntoChatOS:CloudUrl"] = "https://cloud.intochat.io",
            })
            .Build();
        var cloudServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(cloudConfig)
            .AddSingleton<IConnectedComputeRouter>(router)
            .BuildServiceProvider();

        await using var cloudOs = TestOS.Local(o => o.Services = cloudServices)
            .RegisterCatalog()
            .Register<FakeDeviceEnrollment>();

        var cloudInstalled = await cloudOs.Install<SettingsApp>();
        var cloudSettings = cloudInstalled.App;
        using (cloudOs.Edge.Enter(cloudOs.Edge.Person("local", "local", brain)))
        {
            await cloudSettings.Open(new SettingsOpen("general"));
            await cloudSettings.Navigate(new SettingsNavigate("devices"));
            var cloudViewJson = JsonSerializer.Serialize(cloudSettings.Render());
            Assert.Contains("Connected devices", cloudViewJson, StringComparison.Ordinal);
            Assert.Contains("My Windows PC", cloudViewJson, StringComparison.Ordinal);
            Assert.Contains("Link new PC", cloudViewJson, StringComparison.Ordinal);

            await cloudSettings.MintEnrollmentCode(new SettingsMintEnrollmentCode());
            Assert.False(string.IsNullOrWhiteSpace(cloudSettings.State.MintedEnrollmentCode));
            Assert.StartsWith("intochat://enroll?", cloudSettings.State.MintedEnrollmentHandoffUri, StringComparison.Ordinal);

            await cloudSettings.RevokeEnrolledDevice(new SettingsRevokeEnrolledDevice("win-pc-1"));
            Assert.Contains("win-pc-1", FakeDeviceEnrollment.RevokedIds);
        }
    }

    // -------------------------------------------------------------------------
    // 3. Mode B: PC Resource Relay, PathConfinement, Fail-Closed Consent & [PersonOnly] Console
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ModeBRespectsConsentEnforcesPathConfinementAndRequiresPersonOnlyApprovalForConsole()
    {
        var brain = new BrainId("owner");
        var tempRoot = Path.Combine(Path.GetTempPath(), "intochat-modeb-" + Guid.NewGuid().ToString("N")[..8]);
        var consentedDir = Path.Combine(tempRoot, "Projects");
        var unconsentedDir = Path.Combine(tempRoot, "Private");
        Directory.CreateDirectory(consentedDir);
        Directory.CreateDirectory(unconsentedDir);
        await File.WriteAllTextAsync(Path.Combine(consentedDir, "hello.txt"), "Hello from PC!", Ct);
        await File.WriteAllTextAsync(Path.Combine(unconsentedDir, "secret.txt"), "TOP SECRET", Ct);

        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
            var machine = new MachineId("win-pc-modeb");
            var consent = new ClientCodeSandboxConsentStore(initialAllow: true);
            var rootProvider = new StaticRootProvider([consentedDir]);
            var executedCommands = new List<(string Command, string WorkingDir)>();

            var executor = new LocalPcResourceExecutor(
                machine,
                consent,
                rootProvider,
                shellRunner: (cmd, dir, _, _) =>
                {
                    executedCommands.Add((cmd, dir));
                    return Task.FromResult((0, $"ran: {cmd}", ""));
                });

            var relay = new InMemoryModeBRelay();
            using var relayHttp = new HttpClient(relay);
            var credsPath = Path.Combine(tempRoot, "link.bin");
            var creds = new DeviceCredentials(new PlaintextProtector(), credsPath);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await creds.Write(new LinkSecrets(
                new Uri("https://relay.test"),
                "cred-1",
                "access-token",
                clock.GetUtcNow().AddHours(1),
                "refresh-token",
                clock.GetUtcNow().AddDays(7),
                key.ExportPkcs8PrivateKey()), Ct);

            var cloud = new CloudClient(relayHttp, creds, clock);
            var worker = new DeviceWorker(
                cloud,
                creds,
                [],
                machine,
                NullLogger<DeviceWorker>.Instance,
                clock: clock,
                deviceNameProvider: () => "My Windows PC",
                consentStore: consent,
                resourceConsent: consent,
                localResourceExecutor: executor);

            // Consent OFF by default: capabilities must NOT include IWindowsFolderGateway or IConsole
            await worker.Tick(Ct);
            Assert.NotNull(relay.LastAdvertisement);
            Assert.False(relay.LastAdvertisement!.AllowWindowsFolderGateway);
            Assert.False(relay.LastAdvertisement.AllowConsole);
            Assert.DoesNotContain(ClientComputeConsentPolicy.WindowsFolderGatewayCapability, relay.LastAdvertisement.AdvertisedCapabilities ?? []);
            Assert.DoesNotContain(ClientComputeConsentPolicy.ConsoleCapability, relay.LastAdvertisement.AdvertisedCapabilities ?? []);

            // Incoming folder job while consent OFF must fail closed
            relay.EnqueueResourceJob(new PcResourceRequest(
                JobId: "job-denied-off",
                BrainId: brain.Value,
                Capability: ClientComputeConsentPolicy.WindowsFolderGatewayCapability,
                Operation: "read-text",
                PayloadJson: JsonSerializer.Serialize(new FolderResourcePayload("Projects", "hello.txt"), DeviceWireJson.Default.FolderResourcePayload)));
            await worker.Tick(Ct);
            var deniedResult = relay.GetResourceResult("job-denied-off");
            Assert.NotNull(deniedResult);
            Assert.False(deniedResult!.Ok);
            Assert.Contains("consent-disabled", deniedResult.Error, StringComparison.OrdinalIgnoreCase);

            // Enable both Mode B consents -> capabilities & safe folder labels advertised
            consent.SetAllowWindowsFolderGateway(true);
            consent.SetAllowConsole(true);
            await worker.Tick(Ct);
            Assert.True(relay.LastAdvertisement.AllowWindowsFolderGateway);
            Assert.True(relay.LastAdvertisement.AllowConsole);
            Assert.Contains(ClientComputeConsentPolicy.WindowsFolderGatewayCapability, relay.LastAdvertisement.AdvertisedCapabilities ?? []);
            Assert.Contains(ClientComputeConsentPolicy.ConsoleCapability, relay.LastAdvertisement.AdvertisedCapabilities ?? []);
            Assert.Equal(["Projects"], relay.LastAdvertisement.ConsentedFolderLabels ?? []);

            // Wire Cloud-side ConnectedComputeRouter + RemotePcResourceNeuron with direct transport to executor
            var directTransport = new DirectModeBTransport(executor);
            var router = new ConnectedComputeRouter(clock: clock, transport: directTransport, resourceTransport: directTransport);
            await router.RecordHeartbeatAsync(brain, relay.LastAdvertisement, Ct);

            var services = new ServiceCollection()
                .AddSingleton<IConnectedComputeRouter>(router)
                .BuildServiceProvider();
            await using var os = TestOS.Local(o => o.Services = services)
                .Register<RemotePcResourceNeuron>()
                .Register<ConfirmationsNeuron>();

            using var caller = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
            var remotePc = os.Get<IRemotePcResource>(IRemotePcResource.Key(brain.Value));
            var pcs = await remotePc.ListConnectedPcsAsync(Ct);
            var pc = Assert.Single(pcs);
            Assert.Equal("My Windows PC", pc.DeviceName);
            Assert.True(pc.AllowsFolders);
            Assert.True(pc.AllowsConsole);
            Assert.Equal(["Projects"], pc.ConsentedFolders);

            // Consented folder round-trip: read, write, list, search, stat
            Assert.Equal("Hello from PC!", await remotePc.ReadFileTextAsync("Projects", "hello.txt", cancellationToken: Ct));
            await remotePc.WriteFileTextAsync("Projects", "sub/notes.txt", "Created from Cloud Ino", cancellationToken: Ct);
            Assert.Equal("Created from Cloud Ino", await File.ReadAllTextAsync(Path.Combine(consentedDir, "sub", "notes.txt"), Ct));

            var entries = await remotePc.ListFolderAsync("Projects", "", cancellationToken: Ct);
            Assert.Contains(entries, e => e.Name == "hello.txt");
            var searchHits = await remotePc.SearchFolderAsync("Projects", "notes", cancellationToken: Ct);
            Assert.Contains(searchHits, e => e.RelativePath == "sub/notes.txt");
            var stat = await remotePc.StatPathAsync("Projects", "hello.txt", cancellationToken: Ct);
            Assert.NotNull(stat);
            Assert.Equal("hello.txt", stat!.Name);

            // Fail-closed PathConfinement rejections: traversal, UNC, rooted, unconsented root
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remotePc.ReadFileTextAsync("Projects", "../Private/secret.txt", cancellationToken: Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remotePc.ReadFileTextAsync("Projects", @"\\server\share\file.txt", cancellationToken: Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remotePc.ReadFileTextAsync("Projects", Path.Combine(unconsentedDir, "secret.txt"), cancellationToken: Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remotePc.ReadFileTextAsync("Private", "secret.txt", cancellationToken: Ct));

            // [PersonOnly] IConsole approval flow: first call suspends with approval_required, NEVER executes
            var pending = await remotePc.RunConsoleCommandAsync("dotnet --version", rootLabel: "Projects", cancellationToken: Ct);
            Assert.Equal("approval_required", pending.Status);
            Assert.False(string.IsNullOrWhiteSpace(pending.PlanId));
            Assert.Empty(executedCommands);

            // Approve as person via IConfirmationInbox
            var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));
            using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
            {
                await inbox.Resolve(pending.PlanId!, approve: true);
            }

            // Negative checks BEFORE valid execution: mismatched command, rootLabel, or unknown planId must be denied
            var wrongCmd = await remotePc.RunConsoleCommandAsync("whoami", rootLabel: "Projects", approvedPlanId: pending.PlanId, cancellationToken: Ct);
            Assert.Equal("denied", wrongCmd.Status);
            Assert.Empty(executedCommands);

            var wrongRoot = await remotePc.RunConsoleCommandAsync("dotnet --version", rootLabel: "OtherRoot", approvedPlanId: pending.PlanId, cancellationToken: Ct);
            Assert.Equal("denied", wrongRoot.Status);
            Assert.Empty(executedCommands);

            var unknownPlan = await remotePc.RunConsoleCommandAsync("dotnet --version", rootLabel: "Projects", approvedPlanId: "pc-console-forged1234", cancellationToken: Ct);
            Assert.Equal("denied", unknownPlan.Status);
            Assert.Empty(executedCommands);

            // Second call with matching command + rootLabel + approvedPlanId executes on PC once
            var executed = await remotePc.RunConsoleCommandAsync("dotnet --version", rootLabel: "Projects", approvedPlanId: pending.PlanId, cancellationToken: Ct);
            Assert.Equal("completed", executed.Status);
            Assert.Equal(0, executed.ExitCode);
            Assert.Equal("ran: dotnet --version", executed.StandardOutput);
            Assert.Single(executedCommands);

            // Replay of the same approvedPlanId must be denied fail-closed (strict single-use)
            var replayed = await remotePc.RunConsoleCommandAsync("dotnet --version", rootLabel: "Projects", approvedPlanId: pending.PlanId, cancellationToken: Ct);
            Assert.Equal("denied", replayed.Status);
            Assert.Single(executedCommands);

            // Direct LocalPcResourceExecutor security checks: forged token, mismatched command/machine, replay, and cross-cluster BrainId -> local "owner" root lookup
            var forgedResp = await executor.ExecuteAsync(
                new PcResourceRequest(
                    JobId: "job-forged",
                    BrainId: "cloud-tenant-99",
                    Capability: ClientComputeConsentPolicy.ConsoleCapability,
                    Operation: "run",
                    PayloadJson: JsonSerializer.Serialize(new ConsoleResourcePayload("dotnet --version", "Projects", null, "plan-forged"), DeviceWireJson.Default.ConsoleResourcePayload),
                    ApprovalToken: "person-approved:cloud-tenant-99:plan-forged"),
                Ct);
            Assert.False(forgedResp.Ok);
            Assert.Contains("person-approval-required", forgedResp.Error, StringComparison.OrdinalIgnoreCase);

            var wrongMachineProof = LocalPcResourceExecutor.CreateApprovalProof("plan-wrong-machine", "cloud-tenant-99", "other-pc", "dotnet --version", "Projects");
            var wrongMachineResp = await executor.ExecuteAsync(
                new PcResourceRequest(
                    JobId: "job-wrong-machine",
                    BrainId: "cloud-tenant-99",
                    Capability: ClientComputeConsentPolicy.ConsoleCapability,
                    Operation: "run",
                    PayloadJson: JsonSerializer.Serialize(new ConsoleResourcePayload("dotnet --version", "Projects", null, "plan-wrong-machine"), DeviceWireJson.Default.ConsoleResourcePayload),
                    ApprovalToken: wrongMachineProof),
                Ct);
            Assert.False(wrongMachineResp.Ok);
            Assert.Contains("person-approval-required", wrongMachineResp.Error, StringComparison.OrdinalIgnoreCase);

            var validCrossClusterProof = LocalPcResourceExecutor.CreateApprovalProof("plan-cross-cluster", "cloud-tenant-99", machine.Value, "echo hi", "Projects");
            var validCrossClusterReq = new PcResourceRequest(
                JobId: "job-cross-cluster",
                BrainId: "cloud-tenant-99",
                Capability: ClientComputeConsentPolicy.ConsoleCapability,
                Operation: "run",
                PayloadJson: JsonSerializer.Serialize(new ConsoleResourcePayload("echo hi", "Projects", null, "plan-cross-cluster"), DeviceWireJson.Default.ConsoleResourcePayload),
                ApprovalToken: validCrossClusterProof);
            var crossClusterExec = await executor.ExecuteAsync(validCrossClusterReq, Ct);
            Assert.True(crossClusterExec.Ok, crossClusterExec.Error);
            Assert.Equal(IntoChatOSNames.DefaultOwnerBrainId, rootProvider.LastQueriedBrainId);

            // Replaying the exact same valid cryptographic proof + planId directly against LocalPcResourceExecutor must fail closed
            var directReplay = await executor.ExecuteAsync(validCrossClusterReq with { JobId = "job-cross-cluster-replay" }, Ct);
            Assert.False(directReplay.Ok);
            Assert.Contains("person-approval-replay", directReplay.Error, StringComparison.OrdinalIgnoreCase);

            // Cross-cluster folder read also queries local owner roots, not remote cloud brain id
            var crossClusterFolder = await executor.ExecuteAsync(
                new PcResourceRequest(
                    JobId: "job-cross-folder",
                    BrainId: "cloud-tenant-99",
                    Capability: ClientComputeConsentPolicy.WindowsFolderGatewayCapability,
                    Operation: "read-text",
                    PayloadJson: JsonSerializer.Serialize(new FolderResourcePayload("Projects", "hello.txt"), DeviceWireJson.Default.FolderResourcePayload)),
                Ct);
            Assert.True(crossClusterFolder.Ok, crossClusterFolder.Error);
            Assert.Equal(IntoChatOSNames.DefaultOwnerBrainId, rootProvider.LastQueriedBrainId);

            // Live revocation of local PC consent fails closed even when Cloud dispatches
            consent.SetAllowWindowsFolderGateway(false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remotePc.ReadFileTextAsync("Projects", "hello.txt", cancellationToken: Ct));

            // Ino agent definition includes IRemotePcResource & Mode B guidance
            var ino = InoAgentDefinition.For(new UserSettingsState { DisplayName = "Vlad" });
            Assert.Contains(typeof(IRemotePcResource).FullName!, ino.Contracts);
            Assert.Contains("devices_pc_resource_", ino.Instructions, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HostedCloudModeBUsesServiceAssertionAndRevocationClearsLinkBinOnly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "intochat-relay-auth-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var custodyMarker = Path.Combine(tempRoot, "custody.key");
        await File.WriteAllTextAsync(custodyMarker, "keep-me-intact", Ct);

        try
        {
            // 1. Multi-tenant Hosted Cloud OS Mode B dispatch via CloudRelayServiceClient (ServiceAssertion JWT, no link.bin)
            string? capturedAuth = null;
            string? capturedPurpose = null;
            string? capturedBrain = null;
            string? capturedBody = null;
            var signer = new StubServiceAssertionSigner(
                new Uri("https://cloud.intochat.io"),
                (brainId, purpose, _, _, _) =>
                {
                    capturedBrain = brainId;
                    capturedPurpose = purpose;
                    return "eyJ.service.assertion";
                });

            using var serviceHandler = new StubHttpHandler(async req =>
            {
                capturedAuth = req.Headers.Authorization?.ToString();
                capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(Ct);
                var ok = new PcResourceResponse(
                    Ok: true,
                    ResultJson: """{"textContent":"hello-via-service-jwt"}""",
                    ExecutedByMachineId: "win-pc-1");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(ok, DeviceWireJson.Default.PcResourceResponse), Encoding.UTF8, "application/json"),
                };
            });
            using var serviceHttp = new HttpClient(serviceHandler);
            var serviceRelay = new CloudRelayServiceClient(serviceHttp, signer);
            var emptyCreds = new DeviceCredentials(new PlaintextProtector(), Path.Combine(tempRoot, "missing-link.bin"));
            var cloudClient = new CloudClient(serviceHttp, emptyCreds, TimeProvider.System);
            var transport = new RelayConnectedComputeTransport(cloudClient, emptyCreds, serviceRelay: serviceRelay);

            var node = new ConnectedComputeNode(
                BrainId: "tenant-brain-42",
                MachineId: "win-pc-1",
                DeviceName: "My Windows PC",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                Status: ComputeNodeStatus.Online,
                LastHeartbeatAt: DateTimeOffset.UtcNow,
                AllowWindowsFolderGateway: true,
                AdvertisedCapabilities: [ClientComputeConsentPolicy.WindowsFolderGatewayCapability]);

            var resp = await transport.ExecuteResourceAsync(
                new BrainId("tenant-brain-42"),
                node,
                new PcResourceRequest("job-jwt-1", "tenant-brain-42", ClientComputeConsentPolicy.WindowsFolderGatewayCapability, "read-text", """{"rootLabel":"Projects","relativePath":"a.txt"}"""),
                Ct);

            Assert.NotNull(resp);
            Assert.True(resp!.Ok);
            Assert.Equal("Service eyJ.service.assertion", capturedAuth);
            Assert.Equal("tenant-brain-42", capturedBrain);
            Assert.Equal(CloudRelayServiceClient.ComputeDispatchPurpose, capturedPurpose);
            Assert.Contains("\"capability\":\"IWindowsFolderGateway\"", capturedBody!, StringComparison.Ordinal);

            // 2. Server revocation (401 on refresh/call) clears link.bin without touching custody/user state
            var linkPath = Path.Combine(tempRoot, "devices", "link.bin");
            var liveCreds = new DeviceCredentials(new PlaintextProtector(), linkPath);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await liveCreds.Write(new LinkSecrets(
                new Uri("https://cloud.intochat.io"),
                "cred-revoked",
                "access-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "refresh-token",
                DateTimeOffset.UtcNow.AddDays(7),
                key.ExportPkcs8PrivateKey()), Ct);
            Assert.True(File.Exists(linkPath));

            using var revokedHandler = new StubHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            using var revokedHttp = new HttpClient(revokedHandler);
            var revokedCloud = new CloudClient(revokedHttp, liveCreds, TimeProvider.System);
            await Assert.ThrowsAsync<DeviceLinkRevokedException>(() => revokedCloud.StatusAsync(Ct));

            Assert.False(File.Exists(linkPath));
            Assert.Null(await liveCreds.Read(Ct));
            Assert.True(File.Exists(custodyMarker));
            Assert.Equal("keep-me-intact", await File.ReadAllTextAsync(custodyMarker, Ct));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }

    private sealed class PlaintextProtector : IMachineKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }

    private sealed class StaticRootProvider(string[] roots) : IConsentedFolderRootProvider
    {
        public string? LastQueriedBrainId { get; private set; }

        public Task<string[]> GetConsentedRootsAsync(string brainId, CancellationToken cancellationToken = default)
        {
            LastQueriedBrainId = brainId;
            return Task.FromResult(string.Equals(brainId, IntoChatOSNames.DefaultOwnerBrainId, StringComparison.Ordinal) ? roots : []);
        }
    }

    [Neuron("devices.link")]
    private sealed class FakeDeviceLink : Neuron, IDeviceLink
    {
        private static LinkStatus s_status = new(LinkState.NotJoined, null, "Test-PC", null, AllowClientCodeSandbox: true);
        public static int JoinCalls { get; private set; }
        public static int LeaveCalls { get; private set; }

        public static void Reset()
        {
            s_status = new(LinkState.NotJoined, null, "Test-PC", null, AllowClientCodeSandbox: true);
            JoinCalls = 0;
            LeaveCalls = 0;
        }

        public Task<LinkStatus> Join(Uri server, Code code)
        {
            JoinCalls++;
            s_status = s_status with { State = LinkState.Connected, Server = server, CircleId = "circle-1", LastContactedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(s_status);
        }

        public Task<LinkStatus> Status() => Task.FromResult(s_status);

        public Task<LinkStatus> SetAllowClientCodeSandbox(bool allow)
        {
            s_status = s_status with { AllowClientCodeSandbox = allow };
            return Task.FromResult(s_status);
        }

        public Task<LinkStatus> SetAllowWindowsFolderGateway(bool allow)
        {
            s_status = s_status with { AllowWindowsFolderGateway = allow };
            return Task.FromResult(s_status);
        }

        public Task<LinkStatus> SetAllowConsole(bool allow)
        {
            s_status = s_status with { AllowConsole = allow };
            return Task.FromResult(s_status);
        }

        public Task Leave()
        {
            LeaveCalls++;
            s_status = s_status with { State = LinkState.NotJoined, Server = null, CircleId = null };
            return Task.CompletedTask;
        }
    }

    [Neuron("devices.enrollment")]
    private sealed class FakeDeviceEnrollment : Neuron, IDeviceEnrollment
    {
        public static List<string> RevokedIds { get; } = [];
        private static readonly List<EnrolledDevice> s_devices = [];

        public static void Reset()
        {
            RevokedIds.Clear();
            s_devices.Clear();
            s_devices.Add(new EnrolledDevice("win-pc-1", "My Windows PC", "thumb-1", new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero)));
        }

        public Task<Code> Mint() => Task.FromResult(Code.New());
        public Task<EnrolledDevice[]> List() => Task.FromResult(s_devices.ToArray());

        public Task Revoke(string machineId)
        {
            RevokedIds.Add(machineId);
            for (var i = 0; i < s_devices.Count; i++)
            {
                if (s_devices[i].MachineId == machineId)
                {
                    s_devices[i] = s_devices[i] with { Revoked = true };
                }
            }

            return Task.CompletedTask;
        }

        public Task<EnrollmentOutcome> Redeem(string codeHash, string machineId, string keyThumbprint, string machineName)
            => Task.FromResult(EnrollmentOutcome.Rejected);

        public Task<bool> IsLive(string machineId, string keyThumbprint) => Task.FromResult(true);
    }

    private sealed class StubServiceAssertionSigner(
        Uri serviceUri,
        Func<string, string, string, string, ReadOnlySpan<byte>, string> onSign) : IServiceAssertionSigner
    {
        public bool IsConfigured => true;
        public Uri? ServiceUri => serviceUri;

        public string Sign(string brainId, string purpose, string method, string pathAndQuery, ReadOnlySpan<byte> body)
            => onSign(brainId, purpose, method, pathAndQuery, body);
    }

    private sealed class DirectModeBTransport(ILocalPcResourceExecutor executor) : IConnectedComputeTransport, IConnectedResourceTransport
    {
        public Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<ConnectedSandboxResponse?> ExecuteAsync(BrainId brainId, ConnectedComputeNode node, ConnectedSandboxRequest request, CancellationToken ct = default)
            => Task.FromResult<ConnectedSandboxResponse?>(null);

        public async Task<PcResourceResponse?> ExecuteResourceAsync(BrainId brainId, ConnectedComputeNode node, PcResourceRequest request, CancellationToken ct = default)
            => await executor.ExecuteAsync(request, ct).ConfigureAwait(false);
    }

    private sealed class InMemoryModeBRelay : HttpMessageHandler
    {
        private readonly ConcurrentQueue<PcResourceRequest> _resourceJobs = new();
        private readonly ConcurrentDictionary<string, PcResourceResponse> _resourceResults = new(StringComparer.Ordinal);

        public ComputeCapabilityAdvertisement? LastAdvertisement { get; private set; }

        public void EnqueueResourceJob(PcResourceRequest req) => _resourceJobs.Enqueue(req);

        public PcResourceResponse? GetResourceResult(string jobId)
            => _resourceResults.TryGetValue(jobId, out var res) ? res : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path == DeviceRoutes.Models)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Put && path == DeviceRoutes.ComputeCapability)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastAdvertisement = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ComputeCapabilityAdvertisement);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && path == DeviceRoutes.ResourceNext)
            {
                if (_resourceJobs.TryDequeue(out var next))
                {
                    var json = JsonSerializer.Serialize(next, DeviceWireJson.Default.PcResourceRequest);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.StartsWith(CircleRoutes.Prefix + "/compute/resource/", StringComparison.Ordinal) && path.EndsWith("/result", StringComparison.Ordinal))
            {
                var trimmed = path[(CircleRoutes.Prefix + "/compute/resource/").Length..^"/result".Length];
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize(body, DeviceWireJson.Default.PcResourceResponse)!;
                _resourceResults[trimmed] = result;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && path == DeviceRoutes.Next)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
