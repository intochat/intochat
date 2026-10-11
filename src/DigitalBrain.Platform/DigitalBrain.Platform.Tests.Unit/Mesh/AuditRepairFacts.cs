using System.Security.Cryptography;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

public sealed class AuditRepairFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact(Timeout = 60_000)]
    public async Task ANodeThatThrewAwayItsShardFailsAuditAndRepairMovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, directory) = await MeshStoreFacts.StartAsync(9, ct);
        await using var _ = brain;
        var data = RandomNumberGenerator.GetBytes(300);
        var reference = await store.Write(Key, "part/log/1", data, ct);
        var lost = (await MeshStoreFacts.Manifest(brain, Key, reference)).Shards[2];
        await MeshStoreFacts.Shard(brain, lost.NodeId, lost.Cid).Drop();

        var audit = await store.Audit(Key, reference, 0, ct);
        Assert.Contains(audit, r => r.Index == 2 && !r.Passed && r.Reason == "shard missing");

        var repair = await store.Repair(Key, reference, cancellationToken: ct);
        var move = Assert.Single(repair.Moved);
        Assert.Equal((2, lost.NodeId), (move.Index, move.FromNode));
        Assert.NotEqual(lost.NodeId, move.ToNode);

        await directory.SetOnline(lost.NodeId, false);
        Assert.Equal(data, await store.Read(Key, repair.Reference, ct));
        Assert.All(await store.Audit(Key, repair.Reference, 0, ct), r => Assert.True(r.Passed, r.Reason));
    }

    [Fact(Timeout = 60_000)]
    public async Task AHealthySegmentNeedsNoRepair()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await MeshStoreFacts.StartAsync(8, ct);
        await using var _ = brain;
        var reference = await store.Write(Key, "part/log/1", [1, 2, 3], ct);

        var repair = await store.Repair(Key, reference, cancellationToken: ct);

        Assert.Empty(repair.Moved);
        Assert.Equal(reference, repair.Reference);
    }

    [Fact(Timeout = 60_000)]
    public async Task SpentChallengesAreReissuedWithoutMovingShards()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await MeshStoreFacts.StartAsync(8, ct, challengesPerShard: 1);
        await using var _ = brain;
        var reference = await store.Write(Key, "part/log/1", [1, 2, 3], ct);
        Assert.All(await store.Audit(Key, reference, 0, ct), r => Assert.True(r.Passed, r.Reason));
        Assert.All(await store.Audit(Key, reference, 1, ct), r => Assert.Equal("challenges exhausted", r.Reason));

        var repair = await store.Repair(Key, reference, reissue: true, ct);

        Assert.Empty(repair.Moved);
        Assert.NotEqual(reference.ManifestCid, repair.Reference.ManifestCid);
        Assert.All(await store.Audit(Key, repair.Reference, 0, ct), r => Assert.True(r.Passed, r.Reason));
    }
}
