namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class ClusterFacts
{
    [Fact]
    public async Task RestartPointsTheLiveBuildAtTheShaAndRestartsTheSilos()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));

        await grain.Restart("sha0001");

        Assert.Equal("sha0001", cluster.Current);
        Assert.Equal(1, cluster.Restarts);
        var read = await grain.Read();
        Assert.Equal(ClusterStage.Restarting, read.Stage);
        Assert.Equal("sha0001", read.TargetSha);
    }

    [Fact]
    public async Task ARestartIsCompletedByTheNextActivationOnTheNewBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await grain.Restart("sha0001");

        await brain.DeactivateAsync(grain, ct);
        var read = await grain.Read();

        Assert.Equal(ClusterStage.Steady, read.Stage);
        Assert.Equal("sha0001", read.CurrentSha);
        Assert.Null(read.TargetSha);
    }

    [Fact]
    public async Task RestartingToTheSameShaTwiceRestartsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));

        await grain.Restart("sha0001");
        await grain.Restart("sha0001");

        Assert.Equal(1, cluster.Restarts);
    }

    [Fact]
    public async Task OrphanCandidateContainersAreRemovedOnActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeCandidateHost { Orphaned = ["candidate-dead"] };
        await using var brain = await CandidateFacts.StartAsync(host, new FakeClusterHost(), new FakeVerifier(), ct);

        await brain.Get<ICluster>(ICluster.Key("owner")).Read();

        Assert.Contains("orphans", host.Calls);
    }
}
