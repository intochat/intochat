namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class DeploymentRollFacts
{
    [Fact]
    public async Task RollingRestartsEveryInstanceOnceAndEndsSteadyOnTheNewShaAfterTheHandover()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));

        var rolling = await grain.Roll("sha0001");
        await cluster.SelfRestarted.Task.WaitAsync(ct);
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);
        await brain.DeactivateAsync(grain, ct);
        // The next activation (the reminder's, in a cluster) finishes the handover.
        await grain.Read();
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal(DeploymentStage.Rolling, rolling.Stage);
        Assert.Equal("sha0001", steady.Sha);
        Assert.Null((await grain.Read()).Failure);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-0@sha0001"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((DeploymentStage.Steady, "sha0001"), (read.Stage, read.CurrentSha));
        Assert.Empty(read.Remaining);
    }

    [Fact]
    public async Task AHandoverFinishedOnAnotherSiloWaitsForTheHandedOverSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await grain.Roll("sha0001");
        await cluster.SelfRestarted.Task.WaitAsync(ct);

        cluster.Self = "silo-1";
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);
        await brain.DeactivateAsync(grain, ct);
        await grain.Read();
        await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal("silo-0@sha0001", cluster.Waited[^1]);
    }

    [Fact]
    public async Task ARestartTheCliRefusesRollsTheClusterBackToThePreviousSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0", InterruptAt = "silo-1" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Roll("sha0001");
        var back = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.RollingBack), ct);
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal("base000", steady.Sha);
        Assert.Contains("silo-1", (await grain.Read()).Failure, StringComparison.Ordinal);
        Assert.Equal("base000", cluster.Current);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@base000", "mcp-b@base000", "mcp-a@base000"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((DeploymentStage.Steady, "base000"), (read.Stage, read.CurrentSha));
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
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Roll("sha0001");
        var back = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.RollingBack), ct);
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal("base000", steady.Sha);
        Assert.Contains("silo-1", (await grain.Read()).Failure, StringComparison.Ordinal);
        Assert.Equal("base000", cluster.Current);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-1@base000", "mcp-b@base000", "mcp-a@base000"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((DeploymentStage.Steady, "base000"), (read.Stage, read.CurrentSha));
        Assert.NotNull(read.Failure);
    }

    [Fact]
    public async Task RollIsRefusedWhileRestarting()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await grain.Restart("sha0001");

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Roll("sha0002"));
        Assert.Empty(cluster.RestartedSilos);
    }

    [Fact]
    public async Task ALiveSwitchThatFailsRollsTheClusterBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        var gate = cluster.RestartGates["mcp-b"] = new RestartGate();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Roll("sha0001");
        await gate.Reached.Task.WaitAsync(ct);
        // live.json lost the switch and cannot be written again: the next step has to move it.
        cluster.Current = "base000";
        cluster.DieOnSetLiveSha.Add("sha0001");
        gate.Open.SetResult();
        var back = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.RollingBack), ct);
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal("base000", steady.Sha);
        Assert.Contains("live.json could not be written", (await grain.Read()).Failure, StringComparison.Ordinal);
        Assert.Equal("base000", cluster.Current);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@base000", "mcp-b@base000", "mcp-a@base000"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((DeploymentStage.Steady, "base000"), (read.Stage, read.CurrentSha));
    }

    [Fact]
    public async Task RollingResumesOnTheNextActivationWithoutAnotherRoll()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        var gate = cluster.RestartGates["mcp-b"] = new RestartGate();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));

        await grain.Roll("sha0001");
        await gate.Reached.Task.WaitAsync(ct);
        // The activation is lost mid-step; the step it was running drains before the deactivation completes.
        var deactivated = brain.DeactivateAsync(grain, ct);
        gate.Open.SetResult();
        await deactivated;
        await cluster.SelfRestarted.Task.WaitAsync(ct);
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);
        await brain.DeactivateAsync(grain, ct);
        // The next activation (the reminder's, in a cluster) finishes the handover.
        await grain.Read();
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal("sha0001", steady.Sha);
        Assert.Null((await grain.Read()).Failure);
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@sha0001", "silo-0@sha0001"], cluster.RestartedSilos);
        var read = await grain.Read();
        Assert.Equal((DeploymentStage.Steady, "sha0001"), (read.Stage, read.CurrentSha));
        Assert.Empty(read.Remaining);
    }

    [Fact]
    public async Task RollIsRefusedWhileRollingOntoAnotherSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        var gate = cluster.RestartGates["mcp-b"] = new RestartGate();
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await grain.Roll("sha0001");
        await gate.Reached.Task.WaitAsync(ct);

        // Roll queues behind the held step, which then hands on to the next one: the cluster is still Rolling.
        var second = grain.Roll("sha0002");
        gate.Open.SetResult();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => second);

        Assert.Contains("rolling onto sha0001", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryInstanceDrainsAndLeavesTheFrontBeforeItRestarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));

        await grain.Roll("sha0001");
        await cluster.SelfRestarted.Task.WaitAsync(ct);

        string[] expected = [.. new[] { "mcp-a", "mcp-b", "silo-1", "silo-0" }.SelectMany(name => new[] { $"drain {name}", $"out {name}", $"restart {name}" })];
        Assert.Equal(expected, cluster.Steps);
    }

    [Fact]
    public async Task AFrontWithNoOtherRoutableInstanceRollsBackBeforeTheInstanceStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        cluster.NoPeer.Add("silo-1@sha0001");
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Roll("sha0001");
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Equal("base000", steady.Sha);
        Assert.Contains("no other instance is routable", (await grain.Read()).Failure, StringComparison.Ordinal);
        // silo-1 never restarted onto the new build; going back it restarts regardless, then the moved MCP servers.
        Assert.Equal(["mcp-a@sha0001", "mcp-b@sha0001", "silo-1@base000", "mcp-b@base000", "mcp-a@base000"], cluster.RestartedSilos);
    }

    [Fact]
    public async Task RollIsRefusedInAProcessThatIsNotAClusterInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Current = "base000", Self = null };
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await grain.Read();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Roll("sha0001"));

        Assert.Contains("not a cluster instance", refused.Message, StringComparison.Ordinal);
        Assert.Empty(cluster.Steps);
        Assert.Equal(DeploymentStage.Steady, (await grain.Read()).Stage);
    }

    [Fact]
    public async Task ARollBackStopsRatherThanRestartTheOnlyRoutableInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0" };
        cluster.NeverHealthy.Add("silo-1@sha0001");
        // Going back, the front can route nothing but the instance about to restart.
        cluster.NoPeer.Add("mcp-b@base000");
        await using var brain = await CandidateFacts.StartAsync(new FakeCandidateHost(), cluster, new FakeVerifier(), ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Roll("sha0001");
        var steady = await changes.NextAsync(change => change.Stage == nameof(DeploymentStage.Steady), ct);

        Assert.Contains("rolling back", (await grain.Read()).Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp-b@base000", cluster.RestartedSilos);
    }
}
