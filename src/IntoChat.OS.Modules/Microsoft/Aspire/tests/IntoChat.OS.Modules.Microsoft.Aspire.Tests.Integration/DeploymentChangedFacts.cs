namespace IntoChat.OS.Microsoft.Aspire.Tests;

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
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Read();
        var first = await changes.NextAsync(ct: ct);

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
        await using var changes = await brain.Observe<DeploymentChanged>(grain, ct);

        await grain.Read();
        await changes.NextAsync(ct: ct);
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
        await using var early = await brain.Observe<DeploymentChanged>(grain, ct);
        await grain.Read();
        var published = await early.NextAsync(ct: ct);

        var retained = await grain.AsReference<IRetainsSignals>().Retained(nameof(DeploymentChanged), 0);

        var last = Assert.IsType<DeploymentChanged>(Assert.Single(retained).Signal);
        Assert.Equal(published.Silos, last.Silos);
    }
}
