using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.E2E;

/// <summary>
/// True Two-Cluster Paired PC Plane 2 Self-Improving Relay E2E suite (INT-2606 / AB-QA Part 1).
/// Connects <c>OS-Cloud</c> (<see cref="EngineerRunsNeuron"/>, <see cref="ConnectedComputeRouter"/>,
/// <see cref="RelayConnectedComputeTransport"/>, <see cref="CloudBrainRelayClient"/>) and <c>OS-Desktop</c>
/// (<see cref="DeviceWorker"/>, <see cref="ClientCodeSandboxConsentStore"/>, <see cref="LocalConnectedEngineeringExecutor"/>,
/// real temp git repo + real <see cref="WorkTreeService"/>, deterministic <see cref="IEngineeringHarness"/>,
/// <see cref="IEngineerBuildVerifier"/>, <see cref="CandidateLiveVerifier"/>) over the real loopback Kestrel
/// <c>/circle/v1/compute/*</c> HTTP + NDJSON stream relay.
/// </summary>
public sealed class PairedPcPlane2EngineeringE2eFacts
{
    private static readonly BrainId OwnerBrain = new("owner");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "PairedPcPlane2E2E")]
    public async Task Test01_FailClosedConsentRejectsDispatch403EvenWhenCodeSandboxAndAutoAllAreEnabled()
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        await using var env = await Plane2E2eClusterPair.StartAsync(
            relay,
            allowClientCodeSandbox: true,
            consentMode: ComputeConsentMode.AutoAll,
            allowConnectedEngineering: false,
            Ct);

        // 1. OS-Desktop DeviceWorker advertises capability (AllowClientCodeSandbox = true, AutoAll, AllowConnectedEngineering = false).
        await env.DesktopWorker.Tick(Ct);

        // 2. Cloud ConnectedComputeRouter.ResolveEngineeringRouteAsync MUST NOT select the unconsented PC.
        var decision = await env.CloudRouter.ResolveEngineeringRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.CloudSandbox, decision.Target);
        Assert.Null(decision.Node);

        // 3. Direct POST /circle/v1/compute/engineering/dispatch to the real Kestrel relay MUST return 403 Forbidden!
        var dispatchReq = new BrainEngineeringDispatchRequest(
            OwnerBrain.Value,
            env.DesktopMachineId,
            new ConnectedEngineeringRequest(
                JobId: "eng-unconsented-403",
                BrainId: OwnerBrain.Value,
                RunId: "run-unconsented-403",
                RepoSlug: "os",
                Goal: "Should be denied 403",
                PauseAtApprovalGate: true));

        using var rawHttp = new HttpClient();
        var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dispatchReq, DeviceWireJson.Default.BrainEngineeringDispatchRequest));
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, relay.Origin + DeviceRoutes.EngineeringDispatch)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpReq.Headers.Add(LoopbackCloudRelayTestHost.BrainIdHeader, OwnerBrain.Value);
        using var httpRes = await rawHttp.SendAsync(httpReq, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, httpRes.StatusCode);

        // 4. DeviceWorker never executed Plane 2 engineering.
        Assert.Equal(0, env.Harness.ExecuteCount);
    }

    [Fact]
    [Trait("Category", "PairedPcPlane2E2E")]
    public async Task Test02_OptedInPlane2EngineeringRunExecutesRealGitWorktreeStreamsNdjsonAndPausesAtApprovalGate()
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        await using var env = await Plane2E2eClusterPair.StartAsync(
            relay,
            allowClientCodeSandbox: false,
            consentMode: ComputeConsentMode.ExplicitToggle,
            allowConnectedEngineering: true,
            Ct);

        // 1. OS-Desktop DeviceWorker advertises AllowConnectedEngineering == true + "IEngineerRuns" over real HTTP PUT /circle/v1/compute/capability.
        await env.DesktopWorker.Tick(Ct);

        var decision = await env.CloudRouter.ResolveEngineeringRouteAsync(OwnerBrain, Ct);
        Assert.Equal(ComputeExecutionTarget.ConnectedClient, decision.Target);
        Assert.NotNull(decision.Node);
        Assert.Equal(env.DesktopMachineId, decision.Node!.MachineId);
        Assert.True(decision.Node.AllowConnectedEngineering);
        Assert.Contains(ClientComputeConsentPolicy.ConnectedEngineeringCapability, decision.Node.AdvertisedCapabilities ?? []);

        // 2. Run OS-Desktop polling loop in background while OS-Cloud EngineerRunsNeuron dispatches over real HTTP + NDJSON stream!
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var workerPump = Task.Run(async () =>
        {
            while (!workerCts.Token.IsCancellationRequested)
            {
                try
                {
                    await env.DesktopWorker.Tick(workerCts.Token).ConfigureAwait(false);
                    await Task.Delay(30, workerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        EngineerRunSummary summary;
        try
        {
            using var owner = env.CloudOs.Edge.Enter(env.CloudOs.Edge.Person("owner", "owner", OwnerBrain));
            var runs = env.CloudOs.Get<IEngineerRuns>(IEngineerRuns.Key(OwnerBrain.Value));
            summary = await runs.StartEngineerRun(
                new EngineerDelegationRequest(
                    RepoSlug: "os",
                    Goal: "Add Plane 2 E2E telemetry marker",
                    RunId: "run-plane2-e2e-ok"),
                Ct);
        }
        finally
        {
            await workerCts.CancelAsync();
            await workerPump;
        }

        // 3. Assert OS-Cloud EngineerRunsNeuron returned exact SHA-locked, approval-gated ConnectedPC result.
        Assert.True(summary.Ok, summary.SummaryText);
        Assert.Equal("ConnectedPC", summary.Backend);
        Assert.Equal(FlowStatus.Waiting, summary.Status);
        Assert.Equal("approval-gate", summary.CurrentStage);
        Assert.NotNull(summary.WinningCommitSha);
        Assert.Matches("^[0-9a-f]{40}$", summary.WinningCommitSha!);
        Assert.NotNull(summary.Receipt);
        Assert.Equal(summary.WinningCommitSha, summary.Receipt!.CommitSha);
        Assert.True(summary.Receipt.BuildVerified);
        Assert.True(summary.Receipt.LiveVerified);
        Assert.Null(summary.PushedRef);
        Assert.Null(summary.PullRequestUrl);
        Assert.Null(summary.Receipt.PushedRef);
        Assert.Null(summary.Receipt.PullRequestUrl);

        // 4. Verify real NDJSON chunks were streamed over HTTP POST /circle/v1/compute/engineering/{jobId}/stream!
        Assert.Single(relay.CapturedEngineeringStreamChunks);
        var chunks = relay.CapturedEngineeringStreamChunks.Values.Single().ToArray();
        Assert.Contains(chunks, static c => c.Stage == "brainstorm-and-plan");
        Assert.Contains(chunks, static c => c.Stage == "prepare-worktree");
        Assert.Contains(chunks, static c => c.Stage == "implement-tasks");
        Assert.Contains(chunks, static c => c.Stage == "build-and-test");
        Assert.Contains(chunks, static c => c.Stage == "live-verify");
        var terminal = chunks.Last();
        Assert.True(terminal.Done);
        Assert.Equal("approval-gate", terminal.Stage);
        Assert.NotNull(terminal.Result);
        Assert.True(terminal.Result!.PausedAtApprovalGate);

        // 5. Verify real git commit exists in the temp repository and zero remote push occurred.
        var gitShowExit = await RunGitAsync(env.SourceRepoPath, $"cat-file -t {summary.WinningCommitSha}", Ct);
        Assert.Equal("commit", gitShowExit.Trim());
        Assert.Equal(1, env.Harness.ExecuteCount);
        Assert.Contains($"publish:{summary.WinningCommitSha![..12]}", env.CandidateHost.Calls);
        Assert.Contains($"remove:{summary.WinningCommitSha[..12]}", env.CandidateHost.Calls);
    }

    [Theory]
    [InlineData("tamper-sha")]
    [InlineData("tamper-live-verify")]
    [InlineData("tamper-approval-gate")]
    [Trait("Category", "PairedPcPlane2E2E")]
    public async Task Test03_TamperedRemoteReceiptFailsClosedInEngineerRunsNeuron(string tamperMode)
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        relay.OnEngineeringResponseMutator = tamperMode switch
        {
            "tamper-sha" => r => r with
            {
                ChangeSet = r.ChangeSet is null ? null : r.ChangeSet with { CommitSha = "not-a-valid-40-char-sha" },
                Receipt = r.Receipt is null ? null : r.Receipt with { CommitSha = "not-a-valid-40-char-sha" },
            },
            "tamper-live-verify" => r => r with
            {
                Receipt = r.Receipt is null ? null : r.Receipt with { LiveVerified = false },
            },
            "tamper-approval-gate" => r => r with { PausedAtApprovalGate = false },
            _ => throw new ArgumentOutOfRangeException(nameof(tamperMode)),
        };

        await using var env = await Plane2E2eClusterPair.StartAsync(
            relay,
            allowClientCodeSandbox: false,
            consentMode: ComputeConsentMode.ExplicitToggle,
            allowConnectedEngineering: true,
            Ct);

        await env.DesktopWorker.Tick(Ct);

        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var workerPump = Task.Run(async () =>
        {
            while (!workerCts.Token.IsCancellationRequested)
            {
                try
                {
                    await env.DesktopWorker.Tick(workerCts.Token).ConfigureAwait(false);
                    await Task.Delay(30, workerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        EngineerRunSummary summary;
        try
        {
            using var owner = env.CloudOs.Edge.Enter(env.CloudOs.Edge.Person("owner", "owner", OwnerBrain));
            var runs = env.CloudOs.Get<IEngineerRuns>(IEngineerRuns.Key(OwnerBrain.Value));
            summary = await runs.StartEngineerRun(
                new EngineerDelegationRequest(
                    RepoSlug: "os",
                    Goal: $"Verify tampered receipt refusal ({tamperMode})",
                    RunId: $"run-tamper-{tamperMode}"),
                Ct);
        }
        finally
        {
            await workerCts.CancelAsync();
            await workerPump;
        }

        Assert.False(summary.Ok);
        Assert.Equal(FlowStatus.Failed, summary.Status);
        Assert.Contains("fail-closed refusal", summary.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Plane2E2eClusterPair : IAsyncDisposable
    {
        private readonly string _tempRoot;
        private readonly HttpClient _cloudHttp;
        private readonly HttpClient _desktopHttp;
        private readonly HttpClient _liveVerifyHttp;
        private readonly ServiceProvider _cloudServices;

        public required TestOS CloudOs { get; init; }
        public required ConnectedComputeRouter CloudRouter { get; init; }
        public required DeviceWorker DesktopWorker { get; init; }
        public required string DesktopMachineId { get; init; }
        public required string SourceRepoPath { get; init; }
        public required RealWorkTreeModifyingHarness Harness { get; init; }
        public required FakeCandidateHost CandidateHost { get; init; }

        private Plane2E2eClusterPair(
            string tempRoot,
            HttpClient cloudHttp,
            HttpClient desktopHttp,
            HttpClient liveVerifyHttp,
            ServiceProvider cloudServices)
        {
            _tempRoot = tempRoot;
            _cloudHttp = cloudHttp;
            _desktopHttp = desktopHttp;
            _liveVerifyHttp = liveVerifyHttp;
            _cloudServices = cloudServices;
        }

        public static async Task<Plane2E2eClusterPair> StartAsync(
            LoopbackCloudRelayTestHost relay,
            bool allowClientCodeSandbox,
            ComputeConsentMode consentMode,
            bool allowConnectedEngineering,
            CancellationToken ct)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"intochat-plane2-e2e-{Guid.NewGuid():N}");
            var sourceRepoPath = Path.Combine(tempRoot, "repo-os");
            var workTreeHome = Path.Combine(tempRoot, "wt-home");
            var cloudLinkDir = Path.Combine(tempRoot, "cloud-link");
            var desktopLinkDir = Path.Combine(tempRoot, "desktop-link");
            Directory.CreateDirectory(sourceRepoPath);
            Directory.CreateDirectory(workTreeHome);
            Directory.CreateDirectory(cloudLinkDir);
            Directory.CreateDirectory(desktopLinkDir);

            // 1. Initialize a real local git repository with a tracked file so real WorkTreeService creates real worktrees & commits!
            await InitializeTempGitRepoAsync(sourceRepoPath, ct).ConfigureAwait(false);

            var workTree = new WorkTreeService(
                configuration: null,
                options: new WorkTreeOptions
                {
                    HomeDirectory = workTreeHome,
                    Backend = WorkTreeBackendKind.LocalGit,
                    VerifyFormatting = false,
                },
                logger: NullLogger<WorkTreeService>.Instance);
            workTree.RegisterRepository(new RegisteredRepository("os", sourceRepoPath, DefaultBaseBranch: "master"));

            // 2. Wire OS-Desktop (DeviceWorker + ClientCodeSandboxConsentStore + LocalConnectedEngineeringExecutor + real WorkTreeService).
            var desktopHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var desktopCreds = new DeviceCredentials(new PassthroughProtector(), Path.Combine(desktopLinkDir, "link.bin"));
            await WriteLinkSecretsAsync(desktopCreds, relay.BaseUri, "owner", ct).ConfigureAwait(false);
            var desktopCloudClient = new CloudClient(desktopHttp, desktopCreds, TimeProvider.System);
            var desktopMachine = new MachineId(Path.Combine(desktopLinkDir, "machine-id"));

            var consentStore = new ClientCodeSandboxConsentStore(
                initialAllow: allowClientCodeSandbox,
                initialAllowConnectedEngineering: allowConnectedEngineering);

            var harness = new RealWorkTreeModifyingHarness();
            var candidateHost = new FakeCandidateHost();
            var liveVerifyHttp = new HttpClient(new StubLiveHttpHandler());
            var liveVerifier = new CandidateLiveVerifier(candidateHost, liveVerifyHttp, healthBudget: TimeSpan.FromSeconds(5));
            var localExecutor = new LocalConnectedEngineeringExecutor(
                workTree,
                new DeterministicPlanner(),
                new PassingBuildVerifier(),
                harness,
                coder: null,
                candidateHostVerifier: liveVerifier,
                liveVerifier: null);

            var desktopOptions = new StaticOptionsMonitor<DevicesOptions>(new DevicesOptions
            {
                Compute = new ComputeCapabilityOptions
                {
                    ConsentMode = consentMode,
                    AllowClientCodeSandbox = allowClientCodeSandbox,
                    AllowConnectedEngineering = allowConnectedEngineering,
                },
            });

            var desktopWorker = new DeviceWorker(
                desktopCloudClient,
                desktopCreds,
                [],
                desktopMachine,
                NullLogger<DeviceWorker>.Instance,
                clock: TimeProvider.System,
                deviceNameProvider: static () => "Paired E2E Workstation",
                options: desktopOptions,
                consentStore: consentStore,
                resourceConsent: consentStore,
                engineeringConsent: consentStore,
                localEngineeringExecutor: localExecutor);

            // 3. Wire OS-Cloud (EngineerRunsNeuron + ConnectedComputeRouter + RelayConnectedComputeTransport + CloudBrainRelayClient).
            var cloudHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var cloudCreds = new DeviceCredentials(new PassthroughProtector(), Path.Combine(cloudLinkDir, "link.bin"));
            await WriteLinkSecretsAsync(cloudCreds, relay.BaseUri, "owner", ct).ConfigureAwait(false);
            var cloudClient = new CloudClient(cloudHttp, cloudCreds, TimeProvider.System);
            var cloudRelayClient = new CloudBrainRelayClient(cloudHttp, cloudClient, cloudCreds, clock: TimeProvider.System);
            var relayTransport = new RelayConnectedComputeTransport(cloudRelayClient);
            var cloudRouter = new ConnectedComputeRouter(
                relay: cloudRelayClient,
                clock: TimeProvider.System,
                transport: relayTransport,
                engineeringTransport: relayTransport);

            var cloudServices = new ServiceCollection()
                .AddSingleton<IConnectedComputeRouter>(cloudRouter)
                .BuildServiceProvider();

            var cloudOs = TestOS.Local(o => o.Services = cloudServices).Register<EngineerRunsNeuron>();

            return new Plane2E2eClusterPair(tempRoot, cloudHttp, desktopHttp, liveVerifyHttp, cloudServices)
            {
                CloudOs = cloudOs,
                CloudRouter = cloudRouter,
                DesktopWorker = desktopWorker,
                DesktopMachineId = desktopMachine.Value,
                SourceRepoPath = sourceRepoPath,
                Harness = harness,
                CandidateHost = candidateHost,
            };
        }

        public async ValueTask DisposeAsync()
        {
            DesktopWorker.Dispose();
            await CloudOs.DisposeAsync().ConfigureAwait(false);
            await _cloudServices.DisposeAsync().ConfigureAwait(false);
            _cloudHttp.Dispose();
            _desktopHttp.Dispose();
            _liveVerifyHttp.Dispose();

            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    RemoveDirectoryForce(_tempRoot);
                }
            }
            catch
            {
                // Best-effort temp cleanup on Windows when git file handles linger briefly.
            }
        }
    }

    private static async Task WriteLinkSecretsAsync(DeviceCredentials creds, Uri baseUri, string brainId, CancellationToken ct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        await creds.Write(new LinkSecrets(
            Server: baseUri,
            CredentialId: brainId,
            AccessToken: $"{brainId}.access-{Guid.NewGuid():N}",
            AccessExpiresAt: now.AddHours(1),
            RefreshToken: $"{brainId}.refresh-{Guid.NewGuid():N}",
            RefreshExpiresAt: now.AddDays(30),
            PrivateKeyPkcs8: key.ExportPkcs8PrivateKey()), ct).ConfigureAwait(false);
    }

    private static async Task InitializeTempGitRepoAsync(string repoPath, CancellationToken ct)
    {
        var bareOriginPath = repoPath + "-origin.git";
        Directory.CreateDirectory(bareOriginPath);
        await RunGitAsync(bareOriginPath, "init --bare -b master", ct).ConfigureAwait(false);

        await RunGitAsync(repoPath, "init -b master", ct).ConfigureAwait(false);
        await RunGitAsync(repoPath, "config user.name \"Ino Test\"", ct).ConfigureAwait(false);
        await RunGitAsync(repoPath, "config user.email \"ino-test@intochat.io\"", ct).ConfigureAwait(false);

        var flowsDir = Path.Combine(repoPath, "Flows");
        Directory.CreateDirectory(flowsDir);
        await File.WriteAllTextAsync(Path.Combine(flowsDir, "Demo.cs"), "public static class Demo { public const int Version = 1; }\n", ct).ConfigureAwait(false);

        await RunGitAsync(repoPath, "add -A", ct).ConfigureAwait(false);
        await RunGitAsync(repoPath, "commit -m \"Initial seed commit\"", ct).ConfigureAwait(false);
        await RunGitAsync(repoPath, $"remote add origin \"{bareOriginPath}\"", ct).ConfigureAwait(false);
        await RunGitAsync(repoPath, "push -u origin master", ct).ConfigureAwait(false);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    private static void RemoveDirectoryForce(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class DeterministicPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(string repositoryId, string goal, IReadOnlyList<string> allowedExtensions, int maxChangedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                goal,
                "Minimal deterministic Plane 2 edit",
                "Approved",
                [],
                [new EngineerTaskItem("t1", "Update Demo.cs", "Bump version in Flows/Demo.cs", ["Flows/Demo.cs"], [], "Build and live verify pass")]));
    }

    private sealed class RealWorkTreeModifyingHarness : IEngineeringHarness
    {
        private int _executeCount;
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public EngineeringHarnessKind Kind => EngineeringHarnessKind.InoNative;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<EngineeringHarnessResult> ExecuteAsync(
            EngineeringHarnessRequest request,
            Func<OperationProgressed, Task>? onProgress = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executeCount);
            var targetFile = Path.Combine(request.WorktreePath, "Flows", "Demo.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await File.WriteAllTextAsync(
                targetFile,
                $"public static class Demo {{ public const int Version = 2; /* {request.RunId} */ }}\n",
                ct).ConfigureAwait(false);

            return new EngineeringHarnessResult(
                Succeeded: true,
                Harness: Kind,
                Summary: "Applied deterministic Plane 2 edit to Flows/Demo.cs",
                ModelId: "deterministic-e2e",
                TouchedFiles: ["Flows/Demo.cs"],
                DeletedFiles: [],
                CompletedSlices: request.RatifiedPlan.Slices,
                ProgressEvents: []);
        }
    }

    private sealed class PassingBuildVerifier : IEngineerBuildVerifier
    {
        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerBuildReport(
                true,
                true,
                true,
                true,
                0,
                0,
                1,
                0,
                [],
                42,
                "Build & tests passed."));
    }

    private sealed class FakeCandidateHost : ICandidateHost
    {
        public List<string> Calls { get; } = [];
        public Task<PreparedCandidate> Prepare(string baseSha, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
            => Task.FromResult(new PreparedCandidate(baseSha, Path.GetTempPath()));
        public Task BuildGate(string sha, CancellationToken ct) => Task.CompletedTask;
        public Task<string> Publish(string sha, CancellationToken ct) { Calls.Add($"publish:{sha}"); return Task.FromResult(Path.GetTempPath()); }
        public Task<RunningCandidate> Run(string sha, CancellationToken ct) { Calls.Add($"run:{sha}"); return Task.FromResult(new RunningCandidate(sha, new CandidateEndpoints("http://127.0.0.1:52199", "http://127.0.0.1:52199"), $"slot-{sha}")); }
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

    private sealed class PassthroughProtector : IMachineKeyProtector
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
