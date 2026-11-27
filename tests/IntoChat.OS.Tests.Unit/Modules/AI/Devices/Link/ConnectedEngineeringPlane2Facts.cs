using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using IntoChat.Aspire.Verification;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class ConnectedEngineeringPlane2Facts
{
    private static readonly BrainId OwnerBrain = new("owner");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AllowConnectedEngineeringDefaultsFailClosedAndIsStrictlySeparateFromAllowClientCodeSandbox()
    {
        // 1. Policy defaults to false on every platform (fail-closed).
        foreach (var platform in Enum.GetValues<ClientDevicePlatform>())
        {
            Assert.False(ClientComputeConsentPolicy.DefaultAllowConnectedEngineering(platform));
        }

        Assert.False(ClientComputeConsentPolicy.DefaultAllowConnectedEngineering("Microsoft Windows 11"));
        Assert.False(ClientComputeConsentPolicy.DefaultAllowConnectedEngineering("macOS 15"));
        Assert.False(ClientComputeConsentPolicy.DefaultAllowConnectedEngineering("Ubuntu Linux"));
        Assert.False(ClientComputeConsentPolicy.DefaultAllowConnectedEngineering("Android 15"));

        // 2. Wire record defaults AllowConnectedEngineering to false even when AllowClientCodeSandbox == true.
        var adv = new ComputeCapabilityAdvertisement(
            MachineId: "pc-1",
            DeviceName: "Vlad PC",
            ConsentMode: ComputeConsentMode.AutoAll,
            SandboxReady: true,
            SandboxRuntime: "unit-host",
            OsDescription: "Windows",
            MaxConcurrency: 2,
            HeartbeatAt: DateTimeOffset.UtcNow,
            AllowClientCodeSandbox: true,
            AdvertisedCapabilities: [ClientComputeConsentPolicy.CodeSandboxCapability]);

        Assert.False(adv.AllowConnectedEngineering);
        Assert.False(ClientComputeConsentPolicy.IsEligibleForConnectedEngineering(adv.AllowConnectedEngineering, adv.AdvertisedCapabilities));

        // 3. ConnectedComputeNode with AllowClientCodeSandbox == true + AutoAll is eligible for Plane 1, NOT Plane 2.
        var node = new ConnectedComputeNode(
            BrainId: OwnerBrain.Value,
            MachineId: "pc-1",
            DeviceName: "Vlad PC",
            ConsentMode: ComputeConsentMode.AutoAll,
            SandboxReady: true,
            SandboxRuntime: "unit-host",
            Status: ComputeNodeStatus.Online,
            LastHeartbeatAt: DateTimeOffset.UtcNow,
            AllowClientCodeSandbox: true,
            AdvertisedCapabilities: [ClientComputeConsentPolicy.CodeSandboxCapability]);

        Assert.True(node.IsEligibleForSovereignCompute);
        Assert.False(node.AllowConnectedEngineering);
        Assert.False(node.AdvertisesConnectedEngineering);
        Assert.False(node.IsEligibleForConnectedEngineering);

        // 4. Even if capability string is injected, AllowConnectedEngineering == false stays fail-closed.
        var spoofedCap = node with { AdvertisedCapabilities = [ClientComputeConsentPolicy.ConnectedEngineeringCapability] };
        Assert.False(spoofedCap.IsEligibleForConnectedEngineering);

        // 5. Explicitly setting AllowConnectedEngineering == true + capability authorizes Plane 2 even when Plane 1 is OFF.
        var plane2Only = node with
        {
            AllowClientCodeSandbox = false,
            SandboxReady = false,
            ConsentMode = ComputeConsentMode.ExplicitToggle,
            AllowConnectedEngineering = true,
            AdvertisedCapabilities = [ClientComputeConsentPolicy.ConnectedEngineeringCapability],
        };
        Assert.False(plane2Only.IsEligibleForSovereignCompute);
        Assert.True(plane2Only.IsEligibleForConnectedEngineering);

        // 6. Consent store keeps Plane 1 and Plane 2 completely independent.
        var store = new ClientCodeSandboxConsentStore(initialAllow: true);
        Assert.True(store.AllowClientCodeSandbox);
        Assert.False(store.AllowConnectedEngineering);

        store.Set(false);
        Assert.False(store.AllowClientCodeSandbox);
        Assert.False(store.AllowConnectedEngineering);

        store.SetAllowConnectedEngineering(true);
        Assert.False(store.AllowClientCodeSandbox);
        Assert.True(store.AllowConnectedEngineering);

        store.Set(true);
        store.SetAllowConnectedEngineering(false);
        Assert.True(store.AllowClientCodeSandbox);
        Assert.False(store.AllowConnectedEngineering);

        // 7. ComputeCapabilityOptions defaults AllowConnectedEngineering to false.
        Assert.False(new ComputeCapabilityOptions().AllowConnectedEngineering);
    }

    [Fact]
    public async Task DeviceWorkerRefusesPlane2EngineeringWhenConsentDisabledEvenIfCodeSandboxIsEnabled()
    {
        var consent = new ClientCodeSandboxConsentStore(initialAllow: true, initialAllowConnectedEngineering: false);
        var executor = new RecordingEngineeringExecutor();

        await using var harness = await OpenWorkerAsync(consent, executor);
        harness.Server.EnqueueEngineering(NewEngineeringRequest("eng-refused", "run-refused"));

        await harness.Worker.Tick(Ct);

        // Executor was never invoked.
        Assert.Equal(0, executor.StreamingCalls);
        Assert.Equal(0, executor.UnaryCalls);

        // Advertised capability heartbeat reports AllowConnectedEngineering == false and omits IEngineerRuns.
        Assert.NotNull(harness.Server.LastCapability);
        Assert.True(harness.Server.LastCapability!.AllowClientCodeSandbox);
        Assert.False(harness.Server.LastCapability.AllowConnectedEngineering);
        Assert.DoesNotContain(ClientComputeConsentPolicy.ConnectedEngineeringCapability, harness.Server.LastCapability.AdvertisedCapabilities ?? []);

        // Refusal response was reported back to cloud.
        var result = harness.Server.EngineeringResult("eng-refused");
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.StartsWith("consent-disabled", result.Error, StringComparison.Ordinal);
        Assert.Equal(harness.MachineId, result.ExecutedByMachineId);
    }

    [Fact]
    public async Task DeviceWorkerStreamsNdjsonProgressAndReturnsShaLockedChangeSetAndReceiptWhenConsented()
    {
        var consent = new ClientCodeSandboxConsentStore(initialAllow: false, initialAllowConnectedEngineering: true);
        const string expectedSha = "0123456789abcdef0123456789abcdef01234567";
        var workTree = new FakeWorkTree(expectedSha);
        var planner = new FakeEngineerPlanner();
        var harnessImpl = new FakeEngineeringHarness();
        var buildVerifier = new FakeBuildVerifier(succeeded: true);
        var fakeCandidateHost = new FakeCandidateHost();
        using var okHttp = new HttpClient(new StubLiveHttpHandler());
        var liveVerifier = new CandidateLiveVerifier(fakeCandidateHost, okHttp, healthBudget: TimeSpan.FromSeconds(5));

        var localExecutor = new LocalConnectedEngineeringExecutor(
            workTree,
            planner,
            buildVerifier,
            harnessImpl,
            coder: null,
            candidateHostVerifier: liveVerifier,
            liveVerifier: null);

        await using var workerHarness = await OpenWorkerAsync(consent, localExecutor);
        workerHarness.Server.EnqueueEngineering(NewEngineeringRequest("eng-ok", "run-ok"));

        await workerHarness.Worker.Tick(Ct);

        // Heartbeat advertises AllowConnectedEngineering == true + IEngineerRuns even with AllowClientCodeSandbox == false.
        Assert.NotNull(workerHarness.Server.LastCapability);
        Assert.False(workerHarness.Server.LastCapability!.AllowClientCodeSandbox);
        Assert.True(workerHarness.Server.LastCapability.AllowConnectedEngineering);
        Assert.Contains(ClientComputeConsentPolicy.ConnectedEngineeringCapability, workerHarness.Server.LastCapability.AdvertisedCapabilities ?? []);

        // NDJSON chunks streamed for every pipeline stage and paused at approval-gate.
        var chunks = workerHarness.Server.EngineeringStream("eng-ok");
        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, static c => c.Stage == "brainstorm-and-plan");
        Assert.Contains(chunks, static c => c.Stage == "prepare-worktree");
        Assert.Contains(chunks, static c => c.Stage == "implement-tasks");
        Assert.Contains(chunks, static c => c.Stage == "build-and-test");
        Assert.Contains(chunks, static c => c.Stage == "live-verify");

        var terminal = chunks.Last();
        Assert.True(terminal.Done);
        Assert.Equal("approval-gate", terminal.Stage);
        Assert.NotNull(terminal.Result);

        var response = terminal.Result!;
        Assert.True(response.Ok, response.Error);
        Assert.True(response.PausedAtApprovalGate);
        Assert.Equal(workerHarness.MachineId, response.ExecutedByMachineId);
        Assert.NotNull(response.ChangeSet);
        Assert.NotNull(response.Receipt);
        Assert.Equal(expectedSha, response.ChangeSet!.CommitSha);
        Assert.Equal(expectedSha, response.Receipt!.CommitSha);
        Assert.True(response.Receipt.BuildVerified);
        Assert.True(response.Receipt.LiveVerified);
        Assert.Null(response.Receipt.PushedRef);
        Assert.Null(response.Receipt.PullRequestUrl);

        // Paired PC NEVER pushes or opens PR directly — human approval stays in Cloud Ino.
        Assert.Equal(0, workTree.PushCalls);
        Assert.Equal(0, workTree.PrCalls);
        Assert.Contains($"publish:{expectedSha[..12]}", fakeCandidateHost.Calls);
        Assert.Contains($"remove:{expectedSha[..12]}", fakeCandidateHost.Calls);
    }

    [Fact]
    public async Task DeviceWorkerEngineeringStream404FallbackDoesNotDoubleExecute()
    {
        var consent = new ClientCodeSandboxConsentStore(initialAllow: false, initialAllowConnectedEngineering: true);
        var executor = new RecordingEngineeringExecutor();

        await using var harness = await OpenWorkerAsync(consent, executor);
        harness.Server.Return404ForEngineeringStream = true;
        harness.Server.EnqueueEngineering(NewEngineeringRequest("eng-404", "run-404"));

        await harness.Worker.Tick(Ct);

        // Server returned 404 before reading body -> unary fallback executed once, never double-executed.
        Assert.Equal(0, executor.StreamingCalls);
        Assert.Equal(1, executor.UnaryCalls);
        var reported = harness.Server.EngineeringResult("eng-404");
        Assert.NotNull(reported);
        Assert.True(reported!.Ok);
        Assert.Equal(harness.MachineId, reported.ExecutedByMachineId);
    }

    [Fact]
    public async Task ConnectedDeviceCandidateHostAndEngineerRunsNeuronRouteToTier1AndFallBackCleanly()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        const string sha = "abcdef0123456789abcdef0123456789abcdef01";

        ConnectedEngineeringRequest? capturedRequest = null;
        var transport = new ScriptedEngineeringTransport(
            probe: static (_, _, _) => Task.FromResult(true),
            executeEngineering: (_, node, req, _, _) =>
            {
                capturedRequest = req;
                return Task.FromResult<ConnectedEngineeringResponse?>(new ConnectedEngineeringResponse(
                    Ok: true,
                    RunId: req.RunId,
                    FlowId: $"owner/flow.engineer/{req.RunId}",
                    Stage: "approval-gate",
                    PausedAtApprovalGate: true,
                    ChangeSet: new ConnectedEngineeringChangeSet(
                        CommitSha: sha,
                        BaseSha: "1111111111111111111111111111111111111111",
                        BranchName: "ino/engineer-tier1",
                        Repository: req.RepoSlug,
                        ChangedFiles: ["Flows/Demo.cs"],
                        Additions: 5,
                        Deletions: 1,
                        UnifiedDiff: "+// tier1"),
                    Receipt: new EngineerVerificationReceipt(
                        CommitSha: sha,
                        PullRequestUrl: null,
                        PushedRef: null,
                        BuildVerified: true,
                        LiveVerified: true,
                        CandidateHostKind: "ConnectedPC/AgentDevSlotsCandidateHost",
                        VerificationSummary: "LiveVerify=True"),
                    ExecutedByMachineId: node.MachineId));
            });

        var router = new ConnectedComputeRouter(
            clock: clock,
            transport: transport,
            engineeringTransport: transport);

        // 1. Node with ONLY AllowClientCodeSandbox == true is NOT eligible for Tier 1 Plane 2.
        await router.RecordHeartbeatAsync(
            OwnerBrain,
            new ComputeCapabilityAdvertisement(
                MachineId: "pc-sandbox-only",
                DeviceName: "Sandbox Only PC",
                ConsentMode: ComputeConsentMode.AutoAll,
                SandboxReady: true,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow(),
                AllowClientCodeSandbox: true,
                AdvertisedCapabilities: [ClientComputeConsentPolicy.CodeSandboxCapability],
                AllowConnectedEngineering: false),
            Ct);

        var bridge = new ConnectedCandidateRelayBridge(router);
        var fallbackHost = new FakeCandidateHost();
        var candidateHost = new ConnectedDeviceCandidateHost(fallbackHost, bridge, OwnerBrain);

        Assert.Null(await candidateHost.TryResolveTier1DeviceAsync(OwnerBrain, Ct));
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, (await router.ResolveEngineeringRouteAsync(OwnerBrain, Ct)).Target);

        // 2. Register an eligible online Paired PC advertising AllowConnectedEngineering == true -> Tier 1 selected!
        await router.RecordHeartbeatAsync(
            OwnerBrain,
            new ComputeCapabilityAdvertisement(
                MachineId: "pc-eng-1",
                DeviceName: "Vlad Workstation",
                ConsentMode: ComputeConsentMode.ExplicitToggle,
                SandboxReady: false,
                SandboxRuntime: "unit-host",
                OsDescription: "Windows",
                MaxConcurrency: 2,
                HeartbeatAt: clock.GetUtcNow(),
                AllowClientCodeSandbox: false,
                AdvertisedCapabilities: [ClientComputeConsentPolicy.ConnectedEngineeringCapability],
                AllowConnectedEngineering: true),
            Ct);

        var tier1 = await candidateHost.TryResolveTier1DeviceAsync(OwnerBrain, Ct);
        Assert.NotNull(tier1);
        Assert.Equal("pc-eng-1", tier1!.MachineId);
        Assert.Equal(1, candidateHost.Tier1HitCount);

        // 3. Cloud EngineerRunsNeuron routes StartEngineerRun to Tier 1 Paired PC with PauseAtApprovalGate == true.
        var services = new ServiceCollection()
            .AddSingleton<IConnectedComputeRouter>(router)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services).Register<EngineerRunsNeuron>();
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", OwnerBrain));

        var runs = os.Get<IEngineerRuns>(IEngineerRuns.Key(OwnerBrain.Value));
        var summary = await runs.StartEngineerRun(
            new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Tier 1 telemetry"),
            Ct);

        Assert.True(summary.Ok, summary.SummaryText);
        Assert.Equal("ConnectedPC", summary.Backend);
        Assert.Equal(FlowStatus.Waiting, summary.Status);
        Assert.Equal("approval-gate", summary.CurrentStage);
        Assert.Equal(sha, summary.WinningCommitSha);
        Assert.NotNull(summary.Receipt);
        Assert.Equal(sha, summary.Receipt!.CommitSha);
        Assert.True(summary.Receipt.BuildVerified);
        Assert.True(summary.Receipt.LiveVerified);
        Assert.Null(summary.PullRequestUrl);
        Assert.Null(summary.PushedRef);
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.PauseAtApprovalGate);

        // 4. When Paired PC goes offline, Tier 1 probe returns null and ConnectedDeviceCandidateHost falls back cleanly.
        await router.MarkOfflineAsync(OwnerBrain, "pc-eng-1", Ct);
        Assert.Null(await candidateHost.TryResolveTier1DeviceAsync(OwnerBrain, Ct));
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, (await router.ResolveEngineeringRouteAsync(OwnerBrain, Ct)).Target);

        // CandidateHost lifecycle cleanly delegates to fallback host when Tier 1 is offline.
        _ = await candidateHost.Publish("abcdef012345", Ct);
        Assert.Contains("publish:abcdef012345", fallbackHost.Calls);
    }

    private static ConnectedEngineeringRequest NewEngineeringRequest(string jobId, string runId) => new(
        JobId: jobId,
        BrainId: OwnerBrain.Value,
        RunId: runId,
        RepoSlug: "os",
        Goal: "Fix bug on Paired PC",
        PauseAtApprovalGate: true);

    private static async Task<WorkerTestHarness> OpenWorkerAsync(
        ClientCodeSandboxConsentStore consent,
        ILocalEngineeringExecutor engineeringExecutor)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intochat-plane2-worker-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var server = new FakeEngineeringCloudHandler();
        var http = new HttpClient(server);
        var credentials = new DeviceCredentials(new TestXorProtector(), Path.Combine(tempDir, "link.bin"));
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await credentials.Write(new LinkSecrets(
                new Uri("https://cloud.test"),
                "cred-p2",
                "cred-p2.access",
                clock.GetUtcNow().AddHours(1),
                "cred-p2.refresh",
                clock.GetUtcNow().AddDays(30),
                key.ExportPkcs8PrivateKey()), Ct);
        }

        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var cloud = new CloudClient(http, credentials, clock);
        var options = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
        {
            Compute = new ComputeCapabilityOptions
            {
                ConsentMode = ComputeConsentMode.ExplicitToggle,
                AllowClientCodeSandbox = consent.AllowClientCodeSandbox,
                AllowConnectedEngineering = consent.AllowConnectedEngineering,
            },
        });

        var worker = new DeviceWorker(
            cloud,
            credentials,
            [],
            machine,
            NullLogger<DeviceWorker>.Instance,
            clock: clock,
            deviceNameProvider: static () => "Test Desktop",
            options: options,
            consentStore: consent,
            resourceConsent: consent,
            engineeringConsent: consent,
            localEngineeringExecutor: engineeringExecutor);

        return new WorkerTestHarness(tempDir, machine.Value, server, http, worker);
    }

    private sealed class WorkerTestHarness(
        string tempDir,
        string machineId,
        FakeEngineeringCloudHandler server,
        HttpClient http,
        DeviceWorker worker) : IAsyncDisposable
    {
        public string MachineId { get; } = machineId;
        public FakeEngineeringCloudHandler Server { get; } = server;
        public DeviceWorker Worker { get; } = worker;

        public ValueTask DisposeAsync()
        {
            Worker.Dispose();
            http.Dispose();
            Server.Dispose();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEngineeringCloudHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<ConnectedEngineeringRequest> _engineeringQueue = new();
        private readonly ConcurrentDictionary<string, ConnectedEngineeringResponse> _engineeringResults = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<EngineeringStreamChunk>> _engineeringStreams = new(StringComparer.Ordinal);

        public ComputeCapabilityAdvertisement? LastCapability { get; private set; }
        public bool Return404ForEngineeringStream { get; set; }

        public void EnqueueEngineering(ConnectedEngineeringRequest req) => _engineeringQueue.Enqueue(req);

        public ConnectedEngineeringResponse? EngineeringResult(string jobId)
            => _engineeringResults.TryGetValue(jobId, out var r) ? r : null;

        public IReadOnlyList<EngineeringStreamChunk> EngineeringStream(string jobId)
            => _engineeringStreams.TryGetValue(jobId, out var list) ? list : [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Put && path == DeviceRoutes.Models)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Put && path == DeviceRoutes.ComputeCapability)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastCapability = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ComputeCapabilityAdvertisement);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && path == DeviceRoutes.Next)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && path == DeviceRoutes.EngineeringNext)
            {
                if (_engineeringQueue.TryDequeue(out var job))
                {
                    var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.ConnectedEngineeringRequest);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.StartsWith(CircleRoutes.Prefix + "/compute/engineering/", StringComparison.Ordinal) && path.EndsWith("/stream", StringComparison.Ordinal))
            {
                if (Return404ForEngineeringStream)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var jobId = ExtractJobId(path, "/stream");
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var list = new List<EngineeringStreamChunk>();
                foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (JsonSerializer.Deserialize(line, DeviceWireJson.Default.EngineeringStreamChunk) is { } chunk)
                    {
                        list.Add(chunk);
                    }
                }

                _engineeringStreams[jobId] = list;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.StartsWith(CircleRoutes.Prefix + "/compute/engineering/", StringComparison.Ordinal) && path.EndsWith("/result", StringComparison.Ordinal))
            {
                var jobId = ExtractJobId(path, "/result");
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize(body, DeviceWireJson.Default.ConnectedEngineeringResponse);
                if (result is not null)
                {
                    _engineeringResults[jobId] = result;
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string ExtractJobId(string path, string suffix)
        {
            const string prefix = CircleRoutes.Prefix + "/compute/engineering/";
            return Uri.UnescapeDataString(path[prefix.Length..^suffix.Length]);
        }
    }

    private sealed class RecordingEngineeringExecutor : IStreamingLocalEngineeringExecutor
    {
        public int StreamingCalls { get; private set; }
        public int UnaryCalls { get; private set; }

        public Task<ConnectedEngineeringResponse> ExecuteAsync(ConnectedEngineeringRequest request, CancellationToken ct = default)
        {
            UnaryCalls++;
            return Task.FromResult(new ConnectedEngineeringResponse(Ok: true, RunId: request.RunId, Stage: "approval-gate", PausedAtApprovalGate: true));
        }

        public async IAsyncEnumerable<EngineeringStreamChunk> ExecuteStreamingAsync(
            ConnectedEngineeringRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamingCalls++;
            yield return new EngineeringStreamChunk(
                Stage: "approval-gate",
                Progress: "done",
                Done: true,
                Result: new ConnectedEngineeringResponse(Ok: true, RunId: request.RunId, Stage: "approval-gate", PausedAtApprovalGate: true));
            await Task.CompletedTask;
        }
    }

    private sealed class ScriptedEngineeringTransport(
        Func<BrainId, ConnectedComputeNode, CancellationToken, Task<bool>> probe,
        Func<BrainId, ConnectedComputeNode, ConnectedEngineeringRequest, Func<EngineeringStreamChunk, Task>?, CancellationToken, Task<ConnectedEngineeringResponse?>> executeEngineering)
        : IConnectedComputeTransport, IConnectedEngineeringTransport
    {
        public Task<bool> ProbeAsync(BrainId brainId, ConnectedComputeNode node, CancellationToken ct = default)
            => probe(brainId, node, ct);

        public Task<ConnectedSandboxResponse?> ExecuteAsync(BrainId brainId, ConnectedComputeNode node, ConnectedSandboxRequest request, CancellationToken ct = default)
            => Task.FromResult<ConnectedSandboxResponse?>(null);

        public Task<ConnectedEngineeringResponse?> ExecuteEngineeringAsync(
            BrainId brainId,
            ConnectedComputeNode node,
            ConnectedEngineeringRequest request,
            Func<EngineeringStreamChunk, Task>? onProgress = null,
            CancellationToken ct = default)
            => executeEngineering(brainId, node, request, onProgress, ct);
    }

    private sealed class FakeWorkTree(string commitSha) : IWorkTree
    {
        public int PushCalls { get; private set; }
        public int PrCalls { get; private set; }

        public IReadOnlyList<RegisteredRepository> ListRepositories() => [new RegisteredRepository("os", Path.GetTempPath())];
        public void RegisterRepository(RegisteredRepository repository) { }
        public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => new(repositoryId.Value, Path.GetTempPath());

        public Task<WorkTreeLease> PrepareAsync(RepositoryId repositoryId, string runId, string goal, BranchName? baseBranch = null, CancellationToken ct = default)
            => Task.FromResult(new WorkTreeLease(
                RunId: runId,
                RepositoryId: repositoryId.Value,
                SourceRepoPath: Path.GetTempPath(),
                TreePath: Path.GetTempPath(),
                RealGitDirPath: Path.GetTempPath(),
                GitPointerSha256Hex: "deadbeef",
                BaseBranch: baseBranch?.Value ?? "master",
                BaseCommitSha: "1111111111111111111111111111111111111111",
                BranchName: $"ino/pc-{runId}"));

        public Task<ChangeSet> VerifyAndCommitAsync(WorkTreeLease lease, string commitMessage, CancellationToken ct = default)
            => Task.FromResult(ChangeSet.Create(
                CommitSha.Parse(commitSha),
                [RepoPath.Parse("Flows/Demo.cs")],
                "+// fixed on Paired PC",
                additions: 1,
                deletions: 0));

        public Task<string> PushAsync(WorkTreeLease lease, CommitSha approvedSha, CancellationToken ct = default)
        {
            PushCalls++;
            throw new InvalidOperationException("Paired PC relay must pause at approval-gate and never push directly.");
        }

        public Task<string?> CreateDraftPullRequestAsync(WorkTreeLease lease, string prTitle, string prBody, CancellationToken ct = default)
        {
            PrCalls++;
            throw new InvalidOperationException("Paired PC relay must pause at approval-gate and never open PR directly.");
        }

        public Task<PublishedPrResult> PublishAsync(WorkTreeLease lease, CommitSha approvedSha, string prTitle, string prBody, CancellationToken ct = default)
            => throw new InvalidOperationException("Paired PC relay must pause at approval-gate.");

        public Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEngineerPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(string repositoryId, string goal, IReadOnlyList<string> allowedExtensions, int maxChangedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                goal,
                "Minimal surgical fix",
                "Approved",
                [],
                [new EngineerTaskItem("t1", "Apply fix", "Update Flows/Demo.cs", ["Flows/Demo.cs"], [], "Tests pass")]));
    }

    private sealed class FakeEngineeringHarness : IEngineeringHarness
    {
        public EngineeringHarnessKind Kind => EngineeringHarnessKind.InoNative;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<EngineeringHarnessResult> ExecuteAsync(EngineeringHarnessRequest request, Func<OperationProgressed, Task>? onProgress = null, CancellationToken ct = default)
            => Task.FromResult(new EngineeringHarnessResult(
                Succeeded: true,
                Harness: Kind,
                Summary: "Applied fix",
                ModelId: "local-test",
                TouchedFiles: ["Flows/Demo.cs"],
                DeletedFiles: [],
                CompletedSlices: request.RatifiedPlan.Slices,
                ProgressEvents: []));
    }

    private sealed class FakeBuildVerifier(bool succeeded) : IEngineerBuildVerifier
    {
        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerBuildReport(
                succeeded,
                succeeded,
                succeeded,
                succeeded,
                succeeded ? 0 : 1,
                0,
                succeeded ? 1 : 0,
                succeeded ? 0 : 1,
                [],
                120,
                succeeded ? "Build & tests passed." : "Build failed."));
    }

    private sealed class FakeCandidateHost : ICandidateHost
    {
        public List<string> Calls { get; } = [];
        public Task<PreparedCandidate> Prepare(string baseSha, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
            => Task.FromResult(new PreparedCandidate("abcdef012345", Path.GetTempPath()));
        public Task BuildGate(string sha, CancellationToken ct) => Task.CompletedTask;
        public Task<string> Publish(string sha, CancellationToken ct) { Calls.Add($"publish:{sha}"); return Task.FromResult(Path.GetTempPath()); }
        public Task<RunningCandidate> Run(string sha, CancellationToken ct) { Calls.Add($"run:{sha}"); return Task.FromResult(new RunningCandidate(sha, new CandidateEndpoints("http://127.0.0.1:52100", "http://127.0.0.1:52100"), $"slot-{sha}")); }
        public Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct) { Calls.Add($"healthy:{sha}"); return Task.CompletedTask; }
        public Task<string?> Commit(string sha, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task Uncommit(string sha, string? previous, CancellationToken ct) => Task.CompletedTask;
        public Task Remove(string sha, CancellationToken ct) { Calls.Add($"remove:{sha}"); return Task.CompletedTask; }
        public Task<string[]> Orphans(string[] known, CancellationToken ct) => Task.FromResult<string[]>([]);
    }

    private sealed class StubLiveHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Method == HttpMethod.Post
                ? """{"jsonrpc":"2.0","id":1,"result":{"isError":false,"content":[{"type":"text","text":"{\"brainId\":\"owner\"}"}]}}"""
                : """{"status":"Healthy"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TestXorProtector : IMachineKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext];
        public byte[] Unprotect(byte[] ciphertext) => [.. ciphertext];
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
