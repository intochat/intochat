namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class ClusterRollFacts
{
    [Fact]
    public async Task RollingRestartsEverySiloOnceAndEndsSteadyOnTheNewSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        var rolling = await grain.Roll("sha0001");
        await brain.DeactivateAsync(grain, ct);
        var read = await grain.Read();

        Assert.Equal(ClusterStage.Rolling, rolling.Stage);
        Assert.Equal(["silo-0"], rolling.Remaining);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-0@sha0001"], cluster.RestartedSilos);
        Assert.Equal((ClusterStage.Steady, "sha0001"), (read.Stage, read.CurrentSha));
        Assert.Empty(read.Remaining);
        Assert.Equal(ClusterStage.Rolling, (await changes.NextAsync(ct: ct)).Stage);
    }

    [Fact]
    public async Task RollingResumesFromTheRemainingSilosAfterReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0", InterruptAt = "silo-1" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await Assert.ThrowsAnyAsync<Exception>(() => grain.Roll("sha0001"));

        await brain.DeactivateAsync(grain, ct);
        var cut = await grain.Read();
        await grain.Roll("sha0001");

        Assert.Equal(ClusterStage.Rolling, cut.Stage);
        Assert.Equal(["silo-0", "silo-1"], cut.Remaining);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-0@sha0001"], cluster.RestartedSilos);
    }

    [Fact]
    public async Task ASiloThatNeverBecomesHealthyRollsBackToThePreviousSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        cluster.NeverHealthy.Add("silo-1");
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Roll("sha0001"));
        var read = await grain.Read();

        Assert.Contains("silo-1", error.Message, StringComparison.Ordinal);
        Assert.Equal("base000", cluster.Current);
        Assert.Equal("silo-1@base000", cluster.RestartedSilos[^1]);
        Assert.Equal((ClusterStage.Steady, "base000"), (read.Stage, read.CurrentSha));
        Assert.Equal(ClusterStage.Rolling, (await changes.NextAsync(ct: ct)).Stage);
        var steady = await changes.NextAsync(ct: ct);
        Assert.Equal(("base000", ClusterStage.Steady, null), (steady.CurrentSha, steady.Stage, steady.TargetSha));
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
