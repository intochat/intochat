using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class PlacementFacts
{
    [Fact(Timeout = 60_000)]
    public async Task PlacementPrefersDistinctOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var nodes = host.Get<IStorageNodes>(MeshHost.Key("nodes"));
        await nodes.Register("a1", "alice", ct);
        await nodes.Register("a2", "alice", ct);
        await nodes.Register("b1", "bob", ct);
        await nodes.Register("c1", "carol", ct);

        var placed = await nodes.Place(3, [], ct);

        Assert.Equal(["alice", "bob", "carol"], (await nodes.List(ct)).Where(n => placed.Contains(n.NodeId)).Select(n => n.OwnerAccount).Order());
    }

    [Fact(Timeout = 60_000)]
    public async Task TooFewHealthyNodesRefusesInsteadOfLoweringRedundancy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var nodes = host.Get<IStorageNodes>(MeshHost.Key("nodes"));
        await nodes.Register("a1", "alice", ct);
        await nodes.Register("b1", "bob", ct);
        await nodes.SetOnline("b1", false, ct);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => nodes.Place(2, [], ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeFailingAuditsDropsOutOfPlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var nodes = host.Get<IStorageNodes>(MeshHost.Key("nodes"));
        await nodes.Register("a1", "alice", ct);
        await nodes.Register("b1", "bob", ct);
        for (var i = 0; i < 3; i++) { await nodes.ReportAudit("b1", false, ct); }

        Assert.Equal(["a1"], await nodes.Place(1, [], ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => nodes.Place(2, [], ct));
    }
}
