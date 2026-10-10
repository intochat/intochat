namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class ClusterRollFacts
{
    [Fact]
    public async Task RollingRestartsEveryInstanceOnceAndEndsSteadyOnTheNewShaAfterTheHandover()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));

        var rolling = await grain.Roll("sha0001");
        await cluster.SelfRestarted.Task.WaitAsync(ct);
        await brain.DeactivateAsync(grain, ct);
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);
        var steady = await changes.NextAsync(change => change.Stage == ClusterStage.Steady, ct);

        Assert.Equal(ClusterStage.Rolling, rolling.Stage);
        Assert.Equal(("sha0001", null), (steady.CurrentSha, steady.Failure));
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-0@sha0001"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((ClusterStage.Steady, "sha0001"), (read.Stage, read.CurrentSha));
        Assert.Empty(read.Remaining);
    }

    [Fact]
    public async Task AHandoverFinishedOnAnotherSiloWaitsForTheHandedOverSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await grain.Roll("sha0001");
        await cluster.SelfRestarted.Task.WaitAsync(ct);

        cluster.Self = "silo-1";
        await brain.DeactivateAsync(grain, ct);
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);
        await changes.NextAsync(change => change.Stage == ClusterStage.Steady, ct);

        Assert.Equal("silo-0@sha0001", cluster.Waited[^1]);
    }

    [Fact]
    public async Task ARestartTheCliRefusesRollsTheClusterBackToThePreviousSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0", InterruptAt = "silo-1" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        await grain.Roll("sha0001");
        var back = await changes.NextAsync(change => change.Stage == ClusterStage.RollingBack, ct);
        var steady = await changes.NextAsync(change => change.Stage == ClusterStage.Steady, ct);

        Assert.Equal("base000", back.TargetSha);
        Assert.Equal("base000", steady.CurrentSha);
        Assert.Contains("silo-1", steady.Failure, StringComparison.Ordinal);
        Assert.Equal("base000", cluster.Current);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@base000", "mcp-b@base000", "mcp-a@base000"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((ClusterStage.Steady, "base000"), (read.Stage, read.CurrentSha));
        Assert.NotNull(read.Failure);
    }

    [Theory]
    [InlineData("silo-1@sha0001")]
    [InlineData("silo-1@sha0001 exits")]
    public async Task AnInstanceThatNeverBecomesReadyRollsEveryMovedInstanceBack(string never)
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        cluster.NeverHealthy.Add(never);
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        await grain.Roll("sha0001");
        var back = await changes.NextAsync(change => change.Stage == ClusterStage.RollingBack, ct);
        var steady = await changes.NextAsync(change => change.Stage == ClusterStage.Steady, ct);

        Assert.Equal("base000", back.TargetSha);
        Assert.Equal("base000", steady.CurrentSha);
        Assert.Contains("silo-1", steady.Failure, StringComparison.Ordinal);
        Assert.Equal("base000", cluster.Current);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-1@base000", "mcp-b@base000", "mcp-a@base000"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((ClusterStage.Steady, "base000"), (read.Stage, read.CurrentSha));
        Assert.NotNull(read.Failure);
    }

    [Fact]
    public async Task RollIsRefusedWhileRestarting()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await grain.Restart("sha0001");

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Roll("sha0002"));
        Assert.Empty(cluster.RestartedSilos);
    }

    [Fact]
    public async Task RollIsRefusedWhileRollingOntoAnotherSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await grain.Roll("sha0001");

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Roll("sha0002"));
    }
}
