using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Cluster;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;
using IntoChat.OS.Modules.Microsoft.Aspire.Deployment;
using IntoChat.OS.Modules.Microsoft.Aspire.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests.Unit;

public sealed class CandidateAdoptionFacts
{
    private static readonly BrainId Brain = new("owner");

    [Fact]
    public async Task Promote_MovesLiveRef_RestartsCluster_AndFinishes()
    {
        const string oldSha = "old0001";
        const string newSha = "new0002";

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));

        var candidateHost = new FakeCandidateHost(oldSha);
        var verifier = new FakeCandidateVerifier();
        var clusterHost = new FakeClusterHost(oldSha);

        await using var os = CreateOs(candidateHost, verifier, clusterHost);

        // Initial deployment activation observes oldSha.
        var deploymentId = new NeuronId(Brain, "aspire.deployment", IDeployment.Relative);
        var initialDeployment = await os.Get<IDeployment>(deploymentId).Read();
        Assert.Equal(oldSha, initialDeployment.CurrentSha);
        Assert.Equal(DeploymentStage.Steady, initialDeployment.Stage);

        var candidateId = new NeuronId(Brain, "aspire.candidate", ICandidate.Relative(newSha));
        var candidate = os.Get<ICandidate>(candidateId);
        var plan = new VerificationPlan([], ["smoke"], TimeSpan.FromSeconds(10));

        // Propose -> Start -> Verify -> Promote.
        var proposed = await candidate.Propose("feat/loop-fix", "Fix loop issue", plan);
        Assert.Equal(CandidateStage.Proposed, proposed.Stage);

        var started = await candidate.Start();
        Assert.Equal(CandidateStage.Healthy, started.Stage);

        var verified = await candidate.Verify();
        Assert.Equal(CandidateStage.Verified, verified.Stage);

        var promoting = await candidate.Promote();
        Assert.Equal(CandidateStage.Promoting, promoting.Stage);

        // Commit moved live ref to newSha, and deployment called SetLive + RestartSilos.
        Assert.Equal(newSha, candidateHost.LiveRefSha);
        Assert.Contains(newSha, candidateHost.CommittedShas);
        Assert.Contains(newSha, clusterHost.SetLiveCalls);
        Assert.Equal(1, clusterHost.RestartSilosCount);

        // Simulate cluster restart onto newSha: Reactivating DeploymentNeuron runs Reconcile,
        // sees CurrentSha == newSha, settles DeploymentStage.Steady, and calls CandidateNeuron.Finish().
        await os.Restart();

        var settledDeployment = await os.Get<IDeployment>(deploymentId).Read();
        await os.Idle();
        Assert.Equal(DeploymentStage.Steady, settledDeployment.Stage);
        Assert.Equal(newSha, settledDeployment.CurrentSha);

        var promoted = await os.Get<ICandidate>(candidateId).Read();
        Assert.Equal(CandidateStage.Promoted, promoted.Stage);
        Assert.Contains(newSha, candidateHost.RemovedShas);
    }

    [Fact]
    public async Task FailedHealthCheckAfterPromote_AbandonsAndRestoresPreviousLiveRef()
    {
        const string oldSha = "old0001";
        const string newSha = "new0002";

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));

        var candidateHost = new FakeCandidateHost(oldSha);
        var verifier = new FakeCandidateVerifier();
        var clusterHost = new FakeClusterHost(oldSha);

        await using var os = CreateOs(candidateHost, verifier, clusterHost);

        var deploymentId = new NeuronId(Brain, "aspire.deployment", IDeployment.Relative);
        var initialDeployment = await os.Get<IDeployment>(deploymentId).Read();
        Assert.Equal(oldSha, initialDeployment.CurrentSha);

        var candidateId = new NeuronId(Brain, "aspire.candidate", ICandidate.Relative(newSha));
        var candidate = os.Get<ICandidate>(candidateId);
        var plan = new VerificationPlan([], ["smoke"], TimeSpan.FromSeconds(10));

        await candidate.Propose("feat/loop-broken", "Broken candidate", plan);
        await candidate.Start();
        await candidate.Verify();

        var promoting = await candidate.Promote();
        Assert.Equal(CandidateStage.Promoting, promoting.Stage);
        Assert.Equal(newSha, candidateHost.LiveRefSha);
        Assert.Equal(1, clusterHost.RestartSilosCount);

        // Simulate a failed post-promote health check: the cluster comes back on oldSha instead of newSha.
        clusterHost.Current = oldSha;
        await os.Restart();

        var recoveredDeployment = await os.Get<IDeployment>(deploymentId).Read();
        await os.Idle();
        Assert.Equal(DeploymentStage.Steady, recoveredDeployment.Stage);
        Assert.Equal(oldSha, recoveredDeployment.CurrentSha);

        // Reconcile called Abandon -> Uncommit, restoring the live ref back to oldSha and marking candidate Failed.
        var abandoned = await os.Get<ICandidate>(candidateId).Read();
        Assert.Equal(CandidateStage.Failed, abandoned.Stage);
        Assert.Contains(newSha, candidateHost.UncommittedShas);
        Assert.Equal(oldSha, candidateHost.LiveRefSha);
        Assert.Contains(newSha, candidateHost.RemovedShas);
    }

    private static TestOS CreateOs(
        FakeCandidateHost candidateHost,
        FakeCandidateVerifier verifier,
        FakeClusterHost clusterHost)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ICandidateHost>(candidateHost)
            .AddSingleton<ICandidateVerifier>(verifier)
            .AddSingleton<IClusterHost>(clusterHost)
            .AddSingleton<IClusterMembershipFeed>(new FakeClusterMembershipFeed());
        services.AddOptions<AspireOptions>();

        var provider = services.BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = provider);
        os.Register<CandidateNeuron>();
        os.Register<CandidateQueueNeuron>();
        os.Register<DeploymentNeuron>();
        return os;
    }

    private sealed class FakeCandidateHost(string initialLiveSha) : ICandidateHost
    {
        private readonly Dictionary<string, string> _previousBySha = new(StringComparer.Ordinal);

        public string LiveRefSha { get; private set; } = initialLiveSha;
        public List<string> CommittedShas { get; } = [];
        public List<string> UncommittedShas { get; } = [];
        public List<string> RemovedShas { get; } = [];

        public Task<PreparedCandidate> Prepare(string baseSha, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
            => Task.FromResult(new PreparedCandidate("prepared", "/tmp/worktree"));

        public Task BuildGate(string sha, CancellationToken ct) => Task.CompletedTask;

        public Task<string> Publish(string sha, CancellationToken ct) => Task.FromResult($"/tmp/builds/{sha}");

        public Task<RunningCandidate> Run(string sha, CancellationToken ct)
            => Task.FromResult(new RunningCandidate(
                sha,
                new CandidateEndpoints("http://127.0.0.1:6100", "http://127.0.0.1:6100"),
                $"candidate-{sha}"));

        public Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> Commit(string sha, CancellationToken ct)
        {
            var previous = LiveRefSha;
            _previousBySha[sha] = previous;
            LiveRefSha = sha;
            CommittedShas.Add(sha);
            return Task.FromResult<string?>(previous);
        }

        public Task Uncommit(string sha, string? previous, CancellationToken ct)
        {
            UncommittedShas.Add(sha);
            if (string.Equals(LiveRefSha, sha, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(previous))
                {
                    LiveRefSha = previous;
                }
                else if (_previousBySha.TryGetValue(sha, out var parent))
                {
                    LiveRefSha = parent;
                }
            }
            return Task.CompletedTask;
        }

        public Task Remove(string sha, CancellationToken ct)
        {
            RemovedShas.Add(sha);
            return Task.CompletedTask;
        }

        public Task<string[]> Orphans(string[] known, CancellationToken ct) => Task.FromResult(Array.Empty<string>());
    }

    private sealed class FakeCandidateVerifier : ICandidateVerifier
    {
        public Task<VerificationReport> Verify(
            string brainId,
            string sha,
            CandidateEndpoints endpoints,
            VerificationPlan plan,
            CancellationToken ct)
            => Task.FromResult(new VerificationReport(
                Checks: [new VerificationCheck("smoke", true, "ok")],
                Elapsed: TimeSpan.FromMilliseconds(5)));
    }

    private sealed class FakeClusterHost(string initialSha) : IClusterHost
    {
        public string Current { get; set; } = initialSha;
        public List<string> SetLiveCalls { get; } = [];
        public int RestartSilosCount { get; private set; }

        public Task<string> CurrentSha(CancellationToken ct) => Task.FromResult(Current);

        public Task SetLive(string sha, CancellationToken ct)
        {
            SetLiveCalls.Add(sha);
            Current = sha;
            return Task.CompletedTask;
        }

        public Task RestartSilos(CancellationToken ct)
        {
            RestartSilosCount++;
            return Task.CompletedTask;
        }

        public Task RestartInstance(string name, CancellationToken ct) => Task.CompletedTask;
        public Task DrainInstance(string name, CancellationToken ct) => Task.CompletedTask;
        public Task WaitOutOfFront(string name, TimeSpan budget, CancellationToken ct) => Task.CompletedTask;
        public Task WaitInstance(string name, DateTimeOffset since, TimeSpan budget, CancellationToken ct) => Task.CompletedTask;
        public Task<string[]> Instances(CancellationToken ct) => Task.FromResult(new[] { "OperatingSystem-0" });
        public string? Self => "OperatingSystem-0";
        public DateTimeOffset SelfStarted => DateTimeOffset.UtcNow;
        public Task<DateTimeOffset?> StartedAt(string name, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
        public Task<int> Spans(string service, string span, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeClusterMembershipFeed : IClusterMembershipFeed
    {
        public ClusterSnapshot Current => new([], 1);
        public IAsyncDisposable Subscribe(Func<ClusterSnapshot?, Task> changed) => new NoopSubscription();

        private sealed class NoopSubscription : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
