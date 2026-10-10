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

    [Fact(Timeout = 60_000)]
    public async Task DeletingOneObjectKeepsTheIdenticalShardsOfAnotherAuditable()
    {
        var ct = TestContext.Current.CancellationToken;
        // 40 data shards over a 17-byte ciphertext leave 23 all-zero shards per object on 41 nodes,
        // so two objects must place an identical shard on at least one shared node.
        await using var host = await MeshHost.StartAsync(ct, options => { options.DataShards = 40; options.ParityShards = 1; });
        var nodes = host.Get<IStorageNodes>(MeshHost.Key("nodes"));
        for (var i = 0; i < 41; i++) { await nodes.Register($"n{i}", $"owner{i}", ct); }
        var objects = host.Get<IMeshObjects>(MeshHost.Key("objects"));
        await objects.Put("a", [7], Key, ct);
        await objects.Put("b", [8], Key, ct);

        await objects.Delete("a", ct);

        Assert.Equal([8], await objects.Get("b", Key, ct));
        Assert.All(await objects.Audit("b", ct), r => Assert.True(r.Passed, r.Reason));
    }
}
