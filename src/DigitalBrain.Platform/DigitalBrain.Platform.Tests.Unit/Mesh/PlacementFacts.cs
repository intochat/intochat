using DigitalBrain.Platform.Mesh;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

public sealed class PlacementFacts
{
    private static async Task<(ITestBrain Brain, IStorageNodeDirectory Directory)> StartAsync(CancellationToken ct)
    {
        var brain = await TestBrain.Create().StartAsync(ct);
        return (brain, brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key));
    }

    [Fact(Timeout = 60_000)]
    public async Task PlacementPrefersDistinctOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, nodes) = await StartAsync(ct);
        await using var _ = brain;
        await nodes.Register("a1", "alice");
        await nodes.Register("a2", "alice");
        await nodes.Register("b1", "bob");
        await nodes.Register("c1", "carol");

        var placed = await nodes.Place(3, [], 16, []);

        Assert.Equal(["alice", "bob", "carol"], (await nodes.List()).Where(n => placed.Contains(n.NodeId)).Select(n => n.OwnerAccount).Order());
    }

    [Fact(Timeout = 60_000)]
    public async Task ARepairAvoidsTheOwnersOfTheSegmentsOtherShards()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, nodes) = await StartAsync(ct);
        await using var _ = brain;
        await nodes.Register("a1", "alice");
        await nodes.Register("a2", "alice");
        await nodes.Register("b1", "bob");

        Assert.Equal(["b1"], await nodes.Place(1, ["a1"], 16, []));
    }

    [Fact(Timeout = 60_000)]
    public async Task TooFewHealthyNodesRefusesInsteadOfLoweringRedundancy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, nodes) = await StartAsync(ct);
        await using var _ = brain;
        await nodes.Register("a1", "alice");
        await nodes.Register("b1", "bob");
        await nodes.SetOnline("b1", false);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => nodes.Place(2, [], 16, []));
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeFailingAuditsDropsOutOfPlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, nodes) = await StartAsync(ct);
        await using var _ = brain;
        await nodes.Register("a1", "alice");
        await nodes.Register("b1", "bob");
        for (var i = 0; i < 3; i++) { await nodes.ReportAudit("b1", false); }

        Assert.Equal(["a1"], await nodes.Place(1, [], 16, []));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => nodes.Place(2, [], 16, []));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReRegisteringANodeKeepsItsStatusAndReputation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, nodes) = await StartAsync(ct);
        await using var _ = brain;
        await nodes.Register("a1", "alice");
        await nodes.SetOnline("a1", false);
        await nodes.ReportAudit("a1", false);

        await nodes.Register("a1", "alice");

        var node = Assert.Single(await nodes.List());
        Assert.False(node.Online);
        Assert.Equal(0.5, node.Reputation);
    }

    // Q25, T23-6: three volunteer owners and 24 seeds → each volunteer owner exactly once, the rest seeds.
    [Fact(Timeout = 60_000)]
    public async Task PlacementSpreadsAcrossDistinctOwnersWhenTheyExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, nodes) = await StartAsync(ct);
        await using var _ = brain;
        for (var n = 0; n < 24; n++) { await nodes.Register($"seed-{n}", SeedNodeRegistration.Owner); }
        string[] volunteers = [await Volunteers.Register(brain.Grains, "bob", 1 << 20),
            await Volunteers.Register(brain.Grains, "carol", 1 << 20), await Volunteers.Register(brain.Grains, "dave", 1 << 20)];

        var placed = await nodes.Place(24, [], 1024, ["alice"]);

        var owners = (await nodes.List()).ToDictionary(node => node.NodeId, node => node.OwnerAccount);
        Assert.Equal(24, placed.Distinct().Count());
        Assert.All(volunteers, volunteer => Assert.Single(placed, volunteer));
        Assert.Equal(21, placed.Count(id => owners[id] == SeedNodeRegistration.Owner));
    }
}
