namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests;

// The membership is the orleans module's, read from the one in-process silo.
public sealed class DeploymentChangedFacts
{
    private static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(40);

    private static Task<ITestBrain> StartAsync(CancellationToken ct)
        => CandidateFacts.StartAsync(new FakeCandidateHost(), new FakeClusterHost(), new FakeVerifier(), ct, Sampling);

    [Trait("host", "own")]
    [Fact]
    public async Task TheFirstSamplePublishesTheStageTheShaAndTheClustersSilos()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));

        // The activation's first sample is journaled (06-08: a plain neuron's opening signal is read from the brain's log).
        await grain.Read();
        var first = Assert.Single(await brain.History<DeploymentChanged>(grain, ct));

        Assert.Equal(("Steady", "base000"), (first.Stage, first.Sha));
        Assert.Equal("Active", Assert.Single(first.Silos).State);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnchangedMembershipPublishesNothingUntilTheStageMoves()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await grain.Read();
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);
        await Task.Delay(Sampling * 5, ct);
        Assert.Single(await brain.History<DeploymentChanged>(grain, ct));
        await grain.Restart("sha0001");
        var next = await changes.NextAsync(ct: ct);

        Assert.Equal(nameof(DeploymentStage.Restarting), next.Stage);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ALateSubscriberIsSeededWithTheLastDeploymentChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var grain = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await grain.Read();
        var published = Assert.Single(await brain.History<DeploymentChanged>(grain, ct));
        await grain.Restart("sha0001");

        // The runtime keeps the published signal in the brain's log, which seeds a later reader (06-08).
        var last = (await brain.History<DeploymentChanged>(grain, ct))[^1];
        Assert.Equal(published.Silos, last.Silos);
        Assert.Equal(nameof(DeploymentStage.Restarting), last.Stage);
    }
}
