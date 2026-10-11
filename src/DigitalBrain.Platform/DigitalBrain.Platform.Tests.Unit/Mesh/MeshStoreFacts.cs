using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Platform.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

public sealed class MeshStoreFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    internal static async Task<(ITestBrain Brain, IMeshStore Store, IStorageNodeDirectory Directory)> StartAsync(
        int owners, CancellationToken ct, int? challengesPerShard = null, int dataShards = 4, int parityShards = 2)
    {
        var builder = TestBrain.Create()
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", dataShards.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", parityShards.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (challengesPerShard is { } challenges)
        { builder = builder.WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ChallengesPerShard)}", challenges.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        var brain = await builder.StartAsync(ct);
        var directory = brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);
        for (var i = 0; i < owners; i++) { await directory.Register($"n{i}", $"owner{i}"); }
        return (brain, brain.SiloServices.GetRequiredService<IMeshStore>(), directory);
    }

    internal static IStorageShard Shard(ITestBrain brain, string nodeId, string cid) =>
        brain.Grains.GetGrain<IStorageShard>(StorageShards.Key(nodeId, cid));

    internal static async Task<SegmentManifest> Manifest(ITestBrain brain, byte[] key, MeshRef reference)
    {
        var bytes = await Shard(brain, reference.ManifestNodes[0], reference.ManifestCid).Get();
        var plain = SegmentSeal.Open(key, $"mesh-manifest|{reference.SegmentId}", bytes!);
        return brain.SiloServices.GetRequiredService<Orleans.Serialization.Serializer>().Deserialize<SegmentManifest>(plain)!;
    }

    [Fact(Timeout = 60_000)]
    public async Task ASegmentRoundTripsAndNodesHoldOnlyCiphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var secret = Encoding.UTF8.GetBytes("top secret: the cake is a lie. top secret: the cake is a lie.");

        var reference = await store.Write(Key, "part/log/1", secret, ct);

        Assert.Equal(secret, await store.Read(Key, reference, ct));
        var manifest = await Manifest(brain, Key, reference);
        Assert.Equal(6, manifest.Shards.Select(s => s.NodeId).Distinct().Count());
        foreach (var shard in manifest.Shards)
        {
            var bytes = await Shard(brain, shard.NodeId, shard.Cid).Get();
            Assert.DoesNotContain("secret", Encoding.UTF8.GetString(bytes!), StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ASegmentStillReadsWithAsManyShardsLostAsThereIsParity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var data = RandomNumberGenerator.GetBytes(500);
        var reference = await store.Write(Key, "part/log/1", data, ct);
        foreach (var shard in (await Manifest(brain, Key, reference)).Shards.Take(2)) { await Shard(brain, shard.NodeId, shard.Cid).Drop(); }

        Assert.Equal(data, await store.Read(Key, reference, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASeedNodeRefusesBytesThatDoNotMatchTheCidAndTheReadSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var data = RandomNumberGenerator.GetBytes(500);
        var reference = await store.Write(Key, "part/log/1", data, ct);
        var victim = (await Manifest(brain, Key, reference)).Shards[0];
        // Seed nodes cannot hold bytes under the wrong cid; a lying remote node is plan 7's fact (Fetch already
        // treats a cid mismatch as an erasure).
        await Shard(brain, victim.NodeId, victim.Cid).Drop();
        await Assert.ThrowsAsync<InvalidDataException>(() => Shard(brain, victim.NodeId, victim.Cid).Put([9, 9, 9], null));

        Assert.Equal(data, await store.Read(Key, reference, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task WritingTwiceMakesTwoSegmentsAndLeavesTheFirstReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;

        var first = await store.Write(Key, "part/log/1", [1, 2, 3], ct);
        var second = await store.Write(Key, "part/log/2", [4, 5, 6], ct);

        Assert.NotEqual(first.ManifestCid, second.ManifestCid);
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.Read(Key, first, ct));
        Assert.Equal(new byte[] { 4, 5, 6 }, await store.Read(Key, second, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefCannotBeRepointedAtAnotherSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var first = await store.Write(Key, "part/log/1", [1, 2, 3], ct);
        var second = await store.Write(Key, "part/log/2", [4, 5, 6], ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.Read(Key, first with { SegmentId = "part/log/2" }, ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => store.Read(Key, first with { ManifestCid = "sha256:00" }, ct));
        Assert.Equal(new byte[] { 4, 5, 6 }, await store.Read(Key, second, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task TooFewHealthyNodesRefusesTheWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(5, ct);
        await using var _ = brain;

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => store.Write(Key, "part/log/1", [1], ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReadingWithTheWrongKeyFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var reference = await store.Write(Key, "part/log/1", [1, 2, 3], ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.Read(RandomNumberGenerator.GetBytes(32), reference, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoSegmentsOfTheSameShortDataShareNoShard()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(41, ct, dataShards: 40, parityShards: 1);
        await using var _ = brain;

        var a = await Manifest(brain, Key, await store.Write(Key, "part/log/1", [7], ct));
        var b = await Manifest(brain, Key, await store.Write(Key, "part/log/2", [7], ct));

        Assert.Empty(a.Shards.Select(s => s.Cid).Intersect(b.Shards.Select(s => s.Cid)));
    }

    [Fact(Timeout = 60_000)]
    public async Task DiscardingASegmentRemovesItsShardsAndLeavesOthersReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var gone = await store.Write(Key, "part/log/1", [1, 2, 3], ct);
        var kept = await store.Write(Key, "part/log/2", [4, 5, 6], ct);
        var shards = (await Manifest(brain, Key, gone)).Shards;

        await store.Discard(Key, gone, ct);
        await store.Discard(Key, gone, ct);

        foreach (var shard in shards) { Assert.Null(await Shard(brain, shard.NodeId, shard.Cid).Get()); }
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => store.Read(Key, gone, ct));
        Assert.Equal(new byte[] { 4, 5, 6 }, await store.Read(Key, kept, ct));
    }
}
