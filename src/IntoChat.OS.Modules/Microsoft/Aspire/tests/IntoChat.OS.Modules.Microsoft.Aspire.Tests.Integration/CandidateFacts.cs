using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;
using IntoChat.OS.Modules.Microsoft.Aspire.Deployment;
using IntoChat.OS.Modules.Microsoft.Aspire.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests;

public sealed class CandidateFacts
{
    internal static readonly VerificationPlan Plan = new([new("IntoChat.OS.Modules.Apps.IApp", "owner/apps/calculator", "Read", "[]", "*")], [], TimeSpan.FromMinutes(5));

    // Docker, the aspire CLI and the candidate's MCP are process-level seams: the fakes stand in for them.
    internal static Task<ITestBrain> StartAsync(FakeCandidateHost host, FakeClusterHost cluster, FakeVerifier verifier, CancellationToken ct, TimeSpan? sampling = null)
        => TestBrain.Create()
            .WithModule<AspireModule, AspireOptions>(options => options.ClusterSampling = sampling ?? options.ClusterSampling)
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICandidateHost>(host);
                services.AddSingleton<IClusterHost>(cluster);
                services.AddSingleton<ICandidateVerifier>(verifier);
            })
            .StartAsync(ct);

    private static async Task<(ITestBrain Brain, ICandidate Candidate)> ProposedAsync(FakeCandidateHost host, FakeClusterHost cluster, FakeVerifier verifier, CancellationToken ct)
    {
        var brain = await StartAsync(host, cluster, verifier, ct);
        var candidate = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0001"));
        await candidate.Propose("os-runtime", "change", Plan);
        return (brain, candidate);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ACandidateWalksBuildingStartingHealthyVerifiedPromoted()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        await using var brain = await StartAsync(host, cluster, new FakeVerifier(), ct);
        var candidate = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0001"));
        await using var promoted = await brain.Observe<CandidatePromoted>(candidate, ct);

        await candidate.Propose("os-runtime", "change", Plan);
        Assert.Equal(CandidateStage.Healthy, (await candidate.Start()).Stage);
        Assert.Equal(CandidateStage.Verified, (await candidate.Verify()).Stage);
        Assert.Equal(CandidateStage.Promoting, (await candidate.Promote()).Stage);
        await brain.DeactivateAsync(brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)), ct);
        await brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)).Read();

        Assert.Equal(CandidateStage.Promoted, (await candidate.Read()).Stage);
        Assert.Equal("sha0001", (await promoted.NextAsync(ct: ct)).Sha);
        Assert.Equal("sha0001", cluster.Current);
        Assert.Equal(1, cluster.Restarts);
        Assert.Equal(["gate sha0001", "publish sha0001", "run sha0001", "healthy sha0001", "commit sha0001", "remove sha0001"], host.Calls.Where(call => call != "orphans"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PromoteIsRefusedBeforeVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        var (brain, candidate) = await ProposedAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        await using var _ = brain;

        await candidate.Start();

        await Assert.ThrowsAsync<InvalidOperationException>(candidate.Promote);
        Assert.Equal(0, cluster.Restarts);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PromotingTwiceRestartsTheClusterOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        var (brain, candidate) = await ProposedAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        await using var _ = brain;

        await candidate.Start();
        await candidate.Verify();
        await candidate.Promote();
        var again = await candidate.Promote();

        Assert.Equal(CandidateStage.Promoting, again.Stage);
        Assert.Equal(1, cluster.Restarts);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailedBuildGateEndsAtBuildingWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { FailAt = "gate" };
        var (brain, candidate) = await ProposedAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        await using var _ = brain;
        await using var failed = await brain.Observe<CandidateFailed>(candidate, ct);

        var result = await candidate.Start();

        Assert.Equal(CandidateStage.Failed, result.Stage);
        Assert.Equal("gate failed", result.Failure);
        Assert.Equal(CandidateStage.Building, (await failed.NextAsync(ct: ct)).Stage);
        Assert.DoesNotContain("run sha0001", host.Calls);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnhealthyCandidateFailsAtStartingAndIsRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { Healthy = false };
        var (brain, candidate) = await ProposedAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        await using var _ = brain;
        await using var failed = await brain.Observe<CandidateFailed>(candidate, ct);

        var result = await candidate.Start();

        Assert.Equal(CandidateStage.Failed, result.Stage);
        Assert.Equal(CandidateStage.Starting, (await failed.NextAsync(ct: ct)).Stage);
        Assert.Equal("remove sha0001", host.Calls[^1]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AMissedExpectationFailsAtVerifyingAndRemovesTheContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var (brain, candidate) = await ProposedAsync(host, new FakeClusterHost(), new FakeVerifier { Passed = false }, ct);
        await using var _ = brain;

        await candidate.Start();
        var result = await candidate.Verify();

        Assert.Equal(CandidateStage.Failed, result.Stage);
        Assert.Contains("fake", result.Failure);
        Assert.Equal("remove sha0001", host.Calls[^1]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APromotionCutOffByTheRestartEndsWhenTheClusterReactivatesOnTheNewBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        await using var brain = await StartAsync(host, cluster, new FakeVerifier(), ct);
        var index = brain.Get<ICandidates>(ICandidates.Key(brain.Owner.BrainId));
        var candidate = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0001"));
        await using var promoted = await brain.Observe<CandidatePromoted>(candidate, ct);
        await using var ended = await brain.Observe<CandidateChanged>(index, ct);
        await using var healthy = await brain.Observe<CandidateHealthy>(candidate, ct);
        var sha = await index.Propose(new("os-runtime", "change", [], Plan));
        await healthy.NextAsync(ct: ct);
        await candidate.Verify();
        Assert.Equal(CandidateStage.Promoting, (await candidate.Promote()).Stage);
        Assert.Equal(sha, await index.Live());

        await brain.DeactivateAsync(brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)), ct);
        await brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)).Read();

        Assert.Equal(sha, (await promoted.NextAsync(ct: ct)).Sha);
        Assert.Equal(CandidateStage.Promoted, (await candidate.Read()).Stage);
        await ended.NextAsync(change => change.Sha == sha && change.Stage == CandidateStage.Discarded, ct);
        Assert.Null(await index.Live());
        Assert.Equal($"remove {sha}", host.Calls.Last(call => call.StartsWith("remove", StringComparison.Ordinal)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProposalArrivingDuringACutOffPromotionCompletesAndTheQueueMovesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        await using var brain = await StartAsync(host, cluster, new FakeVerifier(), ct);
        var index = brain.Get<ICandidates>(ICandidates.Key(brain.Owner.BrainId));
        var first = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0001"));
        var second = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0002"));
        await using var healthy = await brain.Observe<CandidateHealthy>(first, ct);
        await using var secondStarted = await brain.Observe<CandidateStarted>(second, ct);
        await index.Propose(new("os-runtime", "one", [], Plan));
        await healthy.NextAsync(ct: ct);
        await first.Verify();
        await first.Promote();
        await brain.DeactivateAsync(brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)), ct);

        var queued = await index.Propose(new("os-runtime", "two", [], Plan));

        Assert.Equal("sha0002", queued);
        Assert.Equal("sha0002", (await secondStarted.NextAsync(ct: ct)).Sha);
        Assert.Equal(CandidateStage.Promoted, (await first.Read()).Stage);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnexpectedStartFailureFailsTheCandidateAndTheNextOneStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { RunError = new IOException("disk gone") };
        await using var brain = await StartAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        var index = brain.Get<ICandidates>(ICandidates.Key(brain.Owner.BrainId));
        var first = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0001"));
        var second = brain.Get<ICandidate>(ICandidate.Key(brain.Owner.BrainId, "sha0002"));
        await using var failed = await brain.Observe<CandidateFailed>(first, ct);
        await using var secondStarted = await brain.Observe<CandidateStarted>(second, ct);

        await index.Propose(new("os-runtime", "one", [], Plan));
        await index.Propose(new("os-runtime", "two", [], Plan));

        Assert.Equal(CandidateStage.Starting, (await failed.NextAsync(ct: ct)).Stage);
        Assert.Equal("disk gone", (await first.Read()).Failure);
        Assert.Equal("sha0002", (await secondStarted.NextAsync(ct: ct)).Sha);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ACommitThatFailsFailsThePromotionWithoutRestarting()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        var (brain, candidate) = await ProposedAsync(host, cluster, new FakeVerifier(), ct);
        await using var _ = brain;
        await candidate.Start();
        await candidate.Verify();
        host.FailAt = "commit";

        var result = await candidate.Promote();

        Assert.Equal(CandidateStage.Failed, result.Stage);
        Assert.Contains("commit", result.Failure, StringComparison.Ordinal);
        Assert.Equal(0, cluster.Restarts);
        Assert.Equal("remove sha0001", host.Calls[^1]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARestartThatFailsPutsTheLiveRefAndBuildBackAndFailsThePromotion()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost { DieOnRestart = true };
        var (brain, candidate) = await ProposedAsync(host, cluster, new FakeVerifier(), ct);
        await using var _ = brain;
        await candidate.Start();
        await candidate.Verify();

        var result = await candidate.Promote();

        Assert.Equal(CandidateStage.Failed, result.Stage);
        Assert.Equal("base000", host.Live);
        Assert.Equal("base000", cluster.Current);
        var read = await brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)).Read();
        Assert.Equal((DeploymentStage.Steady, "base000"), (read.Stage, read.CurrentSha));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARestartThatNeverTookFailsThePromotingCandidateOnTheClustersNextActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        var (brain, candidate) = await ProposedAsync(host, cluster, new FakeVerifier(), ct);
        await using var _ = brain;
        await candidate.Start();
        await candidate.Verify();
        await candidate.Promote();
        cluster.Current = "base000";

        await brain.DeactivateAsync(brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)), ct);
        await brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId)).Read();

        Assert.Equal(CandidateStage.Failed, (await candidate.Read()).Stage);
        Assert.Contains("uncommit sha0001 ", host.Calls);
        Assert.NotEqual("sha0001", host.Live);
    }

    // The cluster persisted a restart towards this very sha (a Promote retried after a crash): the activation
    // Promote's own Restart call causes finishes the candidate while Promote is still waiting on it.
    [Trait("host", "own")]
    [Fact]
    public async Task APromoteThatActivatesTheClusterIntoItsOwnFinishDoesNotDeadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        var (brain, candidate) = await ProposedAsync(host, cluster, new FakeVerifier(), ct);
        await using var _ = brain;
        await candidate.Start();
        await candidate.Verify();
        var live = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await live.Restart("sha0001");
        await brain.DeactivateAsync(live, ct);

        var result = await candidate.Promote().WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(CandidateStage.Promoted, result.Stage);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TwoStartsBuildOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var (brain, candidate) = await ProposedAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        await using var _ = brain;

        var first = candidate.Start();
        var second = candidate.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(first, second));

        Assert.Equal(CandidateStage.Healthy, (await candidate.Read()).Stage);
        Assert.Single(host.Calls, call => call == "gate sha0001");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailureWhoseCleanupFailsStillEndsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { Healthy = false, RemoveFails = true };
        var (brain, candidate) = await ProposedAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        await using var _ = brain;

        var result = await candidate.Start();

        Assert.Equal(CandidateStage.Failed, result.Stage);
        Assert.Equal("sha0001 not healthy", result.Failure);
    }
}
