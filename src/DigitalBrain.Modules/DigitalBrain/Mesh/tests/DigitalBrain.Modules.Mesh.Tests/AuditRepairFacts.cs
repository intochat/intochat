using System.Security.Cryptography;
using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class AuditRepairFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact(Timeout = 60_000)]
    public async Task ANodeThatThrewAwayItsShardFailsAuditAndRepairMovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, nodes) = await MeshObjectFacts.StartAsync(9, ct);
        await using var _ = host;
        var data = RandomNumberGenerator.GetBytes(300);
        await objects.Put("doc", data, Key, ct);
        var lost = (await objects.Manifest("doc", ct))!.Shards[2];
        await MeshObjectFacts.Node(host, lost.NodeId).Delete(lost.Cid, ct);

        var audit = await objects.Audit("doc", ct);
        Assert.Contains(audit, r => r.Index == 2 && !r.Passed && r.Reason == "shard missing");

        var repair = await objects.Repair("doc", ct);
        var move = Assert.Single(repair.Moved);
        Assert.Equal((2, lost.NodeId), (move.Index, move.FromNode));
        Assert.NotEqual(lost.NodeId, move.ToNode);

        await nodes.SetOnline(lost.NodeId, false, ct);
        Assert.Equal(data, await objects.Get("doc", Key, ct));
        Assert.All(await objects.Audit("doc", ct), r => Assert.True(r.Passed, r.Reason));
    }

    [Fact(Timeout = 60_000)]
    public async Task AHealthyObjectNeedsNoRepair()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, _) = await MeshObjectFacts.StartAsync(8, ct);
        await using var _ = host;
        await objects.Put("doc", [1, 2, 3], Key, ct);

        Assert.Empty((await objects.Repair("doc", ct)).Moved);
    }
}
