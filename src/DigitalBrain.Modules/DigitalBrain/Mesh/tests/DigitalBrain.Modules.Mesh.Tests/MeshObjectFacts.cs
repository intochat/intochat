using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class MeshObjectFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    internal static async Task<(ModuleBrain Host, IMeshObjects Objects, IStorageNodes Nodes)> StartAsync(int owners, CancellationToken ct)
    {
        var host = await MeshHost.StartAsync(ct, options => { options.DataShards = 4; options.ParityShards = 2; });
        var nodes = host.Get<IStorageNodes>(MeshHost.Key("nodes"));
        for (var i = 0; i < owners; i++) { await nodes.Register($"n{i}", $"owner{i}", ct); }
        return (host, host.Get<IMeshObjects>(MeshHost.Key("objects")), nodes);
    }

    internal static IStorageNode Node(ModuleBrain host, string nodeId) => host.Get<IStorageNode>(MeshKeys.Node(MeshHost.Key("nodes"), nodeId));

    [Fact(Timeout = 60_000)]
    public async Task AnObjectRoundTripsAndNodesHoldOnlyCiphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, _) = await StartAsync(8, ct);
        await using var _ = host;
        var secret = Encoding.UTF8.GetBytes("top secret: the cake is a lie. top secret: the cake is a lie.");

        await objects.Put("doc", secret, Key, ct);

        Assert.Equal(secret, await objects.Get("doc", Key, ct));
        var manifest = await objects.Manifest("doc", ct);
        Assert.Equal(6, manifest!.Shards.Select(s => s.NodeId).Distinct().Count());
        foreach (var shard in manifest.Shards)
        {
            var bytes = await Node(host, shard.NodeId).Get(shard.Cid, ct);
            Assert.DoesNotContain("secret", Encoding.UTF8.GetString(bytes!), StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AnObjectStillReadsWithAsManyShardsLostAsThereIsParity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, _) = await StartAsync(8, ct);
        await using var _ = host;
        var data = RandomNumberGenerator.GetBytes(500);
        await objects.Put("doc", data, Key, ct);
        var manifest = (await objects.Manifest("doc", ct))!;
        foreach (var shard in manifest.Shards.Take(2)) { await Node(host, shard.NodeId).Delete(shard.Cid, ct); }

        Assert.Equal(data, await objects.Get("doc", Key, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task RewritingAnObjectRemovesTheOldVersionsShards()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, _) = await StartAsync(8, ct);
        await using var _ = host;
        await objects.Put("doc", [1, 2, 3], Key, ct);
        var old = (await objects.Manifest("doc", ct))!;

        Assert.Equal(2, await objects.Put("doc", [4, 5, 6], Key, ct));

        foreach (var shard in old.Shards) { Assert.Null(await Node(host, shard.NodeId).Get(shard.Cid, ct)); }
        Assert.Equal(new byte[] { 4, 5, 6 }, await objects.Get("doc", Key, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task TooFewOwnersRefusesTheWriteAndSavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, _) = await StartAsync(5, ct);
        await using var _ = host;

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => objects.Put("doc", [1], Key, ct));
        Assert.Null(await objects.Manifest("doc", ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReadingWithTheWrongKeyFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, objects, _) = await StartAsync(8, ct);
        await using var _ = host;
        await objects.Put("doc", [1, 2, 3], Key, ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => objects.Get("doc", RandomNumberGenerator.GetBytes(32), ct));
    }
}
