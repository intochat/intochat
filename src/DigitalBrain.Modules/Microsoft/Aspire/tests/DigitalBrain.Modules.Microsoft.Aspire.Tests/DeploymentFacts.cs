namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class DeploymentFacts
{
    [Fact]
    public async Task RestartPointsTheLiveBuildAtTheShaAndRestartsTheSilos()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key("owner"));

        await grain.Restart("sha0001");

        Assert.Equal("sha0001", cluster.Current);
        Assert.Equal(1, cluster.Restarts);
        var read = await grain.Read();
        Assert.Equal(DeploymentStage.Restarting, read.Stage);
        Assert.Equal("sha0001", read.TargetSha);
    }

    [Fact]
    public async Task ARestartIsCompletedByTheNextActivationOnTheNewBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key("owner"));
        await grain.Restart("sha0001");

        await brain.DeactivateAsync(grain, ct);
        var read = await grain.Read();

        Assert.Equal(DeploymentStage.Steady, read.Stage);
        Assert.Equal("sha0001", read.CurrentSha);
        Assert.Null(read.TargetSha);
    }

    // The restart never took (the silo came back on another build): the next activation settles on what runs.
    [Fact]
    public async Task AnActivationOnAnotherBuildThanTheTargetSettlesOnWhatRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key("owner"));
        await grain.Restart("sha0001");
        cluster.Current = "sha0002";

        await brain.DeactivateAsync(grain, ct);
        var read = await grain.Read();

        Assert.Equal((DeploymentStage.Steady, "sha0002"), (read.Stage, read.CurrentSha));
        Assert.Null(read.TargetSha);
    }

    [Fact]
    public async Task RestartingToTheSameShaTwiceRestartsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key("owner"));

        await grain.Restart("sha0001");
        await grain.Restart("sha0001");

        Assert.Equal(1, cluster.Restarts);
    }

    [Fact]
    public async Task OrphanCandidateContainersAreRemovedOnActivationKeepingTheLiveCandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { Orphaned = ["candidate-dead"] };
        await using var brain = await CandidateFacts.StartAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);
        var sha = await brain.Get<ICandidates>(ICandidates.Key("owner")).Propose(new("b", "one", [], CandidateFacts.Plan));
        var grain = brain.Get<IDeployment>(IDeployment.Key("owner"));

        await brain.DeactivateAsync(grain, ct);
        await grain.Read();

        Assert.Contains("orphans", host.Calls);
        Assert.Equal([sha], host.OrphansKnown);
    }

    [Fact]
    public async Task AFailingOrphanSweepDoesNotFailTheActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { FailAt = "orphans" };
        await using var brain = await CandidateFacts.StartAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);

        var read = await brain.Get<IDeployment>(IDeployment.Key("owner")).Read();

        Assert.Equal(DeploymentStage.Steady, read.Stage);
        Assert.Contains("orphans", host.Calls);
    }
}
