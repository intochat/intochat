using DigitalBrain.Microsoft.Aspire.Candidates;
using DigitalBrain.Microsoft.Aspire.Cluster;
using DigitalBrain.Microsoft.Aspire.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class CandidateFacts
{
    internal static readonly VerificationPlan Plan = new([new("DigitalBrain.Apps.IApp", "owner/apps/calculator", "Read", "[]", "*")], [], TimeSpan.FromMinutes(5));

    internal static Task<ModuleBrain> StartAsync(FakeCandidateHost host, FakeClusterHost cluster, FakeVerifier verifier, CancellationToken ct)
        => ModuleTest.Create().WithModule<AspireModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddSingleton<ICandidateHost>(host);
                silo.Services.AddSingleton<IClusterHost>(cluster);
                silo.Services.AddSingleton<ICandidateVerifier>(verifier);
            })
            .StartAsync(ct);

    private static async Task<(ModuleBrain Brain, ICandidate Candidate)> ProposedAsync(FakeCandidateHost host, FakeClusterHost cluster, FakeVerifier verifier, CancellationToken ct)
    {
        var brain = await StartAsync(host, cluster, verifier, ct);
        var candidate = brain.Get<ICandidate>(ICandidate.Key("owner", "sha0001"));
        await candidate.Propose("os-runtime", "change", Plan);
        return (brain, candidate);
    }

    [Fact]
    public async Task ACandidateWalksBuildingStartingHealthyVerifiedPromoted()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost();
        var cluster = new FakeClusterHost();
        await using var brain = await StartAsync(host, cluster, new FakeVerifier(), ct);
        var candidate = brain.Get<ICandidate>(ICandidate.Key("owner", "sha0001"));
        await using var promoted = await brain.Observe<CandidatePromoted>(candidate, ct);

        await candidate.Propose("os-runtime", "change", Plan);
        Assert.Equal(CandidateStage.Healthy, (await candidate.Start()).Stage);
        Assert.Equal(CandidateStage.Verified, (await candidate.Verify()).Stage);
        var final = await candidate.Promote();

        Assert.Equal(CandidateStage.Promoted, final.Stage);
        Assert.Equal("sha0001", (await promoted.NextAsync(ct: ct)).Sha);
        Assert.Equal("sha0001", cluster.Current);
        Assert.Equal(1, cluster.Restarts);
        Assert.Equal(["gate sha0001", "publish sha0001", "run sha0001", "healthy sha0001", "commit sha0001 os-runtime", "remove sha0001"], host.Calls.Where(call => call != "orphans"));
    }

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

        Assert.Equal(CandidateStage.Promoted, again.Stage);
        Assert.Equal(1, cluster.Restarts);
    }

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
}
