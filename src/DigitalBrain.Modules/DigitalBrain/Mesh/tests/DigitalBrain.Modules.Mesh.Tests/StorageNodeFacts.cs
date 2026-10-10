using System.Security.Cryptography;
using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class StorageNodeFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AShardIsStoredUnderItsContentIdAndProvable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var node = host.Get<IStorageNode>(MeshHost.Key("nodes/seed-1"));
        var shard = RandomNumberGenerator.GetBytes(64);
        var cid = StorageProof.ContentId(shard);

        await node.Put(cid, shard, ct);

        Assert.Equal(shard, await node.Get(cid, ct));
        var nonce = RandomNumberGenerator.GetBytes(16);
        Assert.Equal(StorageProof.Compute(nonce, shard), await node.Prove(cid, nonce, ct));
        Assert.Equal(64, await node.BytesStored(ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task AShardWhoseBytesDoNotMatchItsIdIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var node = host.Get<IStorageNode>(MeshHost.Key("nodes/seed-1"));

        await Assert.ThrowsAsync<InvalidDataException>(() => node.Put(StorageProof.ContentId([1]), [2], ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeletedShardCanNoLongerBeProven()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var node = host.Get<IStorageNode>(MeshHost.Key("nodes/seed-1"));
        byte[] shard = [9, 9, 9];
        var cid = StorageProof.ContentId(shard);
        await node.Put(cid, shard, ct);

        await node.Delete(cid, ct);

        Assert.Null(await node.Prove(cid, [1], ct));
        Assert.Null(await node.Get(cid, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task AShardPutTwiceSurvivesOneDeleteAndGoesWithTheSecond()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await MeshHost.StartAsync(ct);
        var node = host.Get<IStorageNode>(MeshHost.Key("nodes/seed-1"));
        byte[] shard = [0, 0, 0, 0];
        var cid = StorageProof.ContentId(shard);
        await node.Put(cid, shard, ct);
        await node.Put(cid, shard, ct);

        await node.Delete(cid, ct);

        Assert.Equal(shard, await node.Get(cid, ct));
        Assert.Equal(StorageProof.Compute([1], shard), await node.Prove(cid, [1], ct));
        Assert.Equal(4, await node.BytesStored(ct));

        await node.Delete(cid, ct);

        Assert.Null(await node.Get(cid, ct));
        Assert.Equal(0, await node.BytesStored(ct));
    }
}
