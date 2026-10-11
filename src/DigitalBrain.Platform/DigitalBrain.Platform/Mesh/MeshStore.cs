using System.Security.Cryptography;
using DigitalBrain.Framework.Mesh;
using Microsoft.Extensions.Options;
using DigitalBrain.Platform.Trust;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Mesh;

// Runs in the caller's silo: the key and the plaintext never leave it. A segment is sealed, erasure-coded and
// scattered; its manifest (shard cids, nodes and challenges) is sealed with the same key and replicated, and the
// ref pins the manifest by content id. Writes create, they never overwrite, so a failure leaves only unreachable
// shards for the reachability sweep, never a half-changed segment.
internal sealed class MeshStore(INodeDirectoryClient directory, Serializer serializer, IOptions<MeshOptions> options) : IMeshStore
{
    private const int MinShardSize = 16;

    private INodeDirectoryClient Directory => directory;

    public async Task<MeshRef> Write(byte[] key, string segmentId, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var settings = options.Value;
        var rs = new ReedSolomon(settings.DataShards, settings.ParityShards);
        var sealedData = SegmentSeal.Close(key, DataContext(segmentId), plaintext);
        var shards = rs.Encode(Padded(sealedData, rs.DataShards), out var shardSize);
        var nodes = await Directory.Place(rs.TotalShards, [], shardSize);
        cancellationToken.ThrowIfCancellationRequested();

        var locations = await Task.WhenAll(Enumerable.Range(0, rs.TotalShards).Select(i => Store(i, nodes[i], shards[i])));
        var manifest = new SegmentManifest
        {
            DataShards = rs.DataShards, ParityShards = rs.ParityShards, ShardSize = shardSize, SealedLength = sealedData.Length, Shards = locations,
        };
        return await WriteManifest(key, segmentId, manifest, cancellationToken);
    }

    public async Task<byte[]> Read(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifest(key, reference, cancellationToken);
        var (shards, _) = await Fetch(manifest, await Health());
        var rs = new ReedSolomon(manifest.DataShards, manifest.ParityShards);
        var sealedData = rs.Join(rs.Reconstruct(shards, manifest.ShardSize), manifest.SealedLength);
        return SegmentSeal.Open(key, DataContext(reference.SegmentId), sealedData);
    }

    public async Task<ShardAudit[]> Audit(byte[] key, MeshRef reference, int round, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(round);
        var manifest = await ReadManifest(key, reference, cancellationToken);
        return await Task.WhenAll(manifest.Shards.Select(async shard =>
        {
            if (round >= shard.Challenges.Length) { return new ShardAudit(shard.Index, shard.NodeId, false, "challenges exhausted"); }
            var challenge = shard.Challenges[round];
            var proof = await directory.Prove(shard.NodeId, shard.Cid, challenge.Nonce);
            var passed = proof is not null && CryptographicOperations.FixedTimeEquals(proof, challenge.Expected);
            await Directory.ReportAudit(new AuditReport(shard.NodeId, reference.ManifestCid, round, passed, manifest.ShardSize));
            return new ShardAudit(shard.Index, shard.NodeId, passed, proof is null ? "shard missing" : passed ? "ok" : "wrong proof");
        }));
    }

    public async Task<MeshRepair> Repair(byte[] key, MeshRef reference, bool reissue = false, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifest(key, reference, cancellationToken);
        var health = await Health();
        var (shards, unreadable) = await Fetch(manifest, health);
        var bad = unreadable
            .Concat(manifest.Shards.Where(s => !health.TryGetValue(s.NodeId, out var node) || !node.Usable).Select(s => s.Index))
            .Distinct().Order().ToArray();
        if (bad.Length == 0 && !reissue) { return new MeshRepair(reference, []); }

        var rs = new ReedSolomon(manifest.DataShards, manifest.ParityShards);
        var full = rs.Reconstruct(shards, manifest.ShardSize);
        var targets = bad.Length == 0 ? [] : await Directory.Place(bad.Length, [.. manifest.Shards.Select(s => s.NodeId)], manifest.ShardSize);
        var moved = bad.Select((index, i) => new ShardMove(index, manifest.Shards[index].NodeId, targets[i])).ToArray();
        var nodeOf = manifest.Shards.Select(s => s.NodeId).ToArray();
        foreach (var move in moved) { nodeOf[move.Index] = move.ToNode; }

        var locations = await Task.WhenAll(Enumerable.Range(0, rs.TotalShards).Select(i => Store(i, nodeOf[i], full[i])));
        var repaired = new SegmentManifest
        {
            DataShards = manifest.DataShards, ParityShards = manifest.ParityShards, ShardSize = manifest.ShardSize,
            SealedLength = manifest.SealedLength, Shards = locations,
        };
        return new MeshRepair(await WriteManifest(key, reference.SegmentId, repaired, cancellationToken), moved);
    }

    // Padding is never read (Join stops at SealedLength); random bytes keep every segment's shards unique. A shard is
    // at least MinShardSize bytes, since one-byte shards of different segments would collide by chance.
    private static byte[] Padded(byte[] sealedData, int dataShards)
    {
        var shardSize = Math.Max(MinShardSize, (sealedData.Length + dataShards - 1) / dataShards);
        var padded = new byte[shardSize * dataShards];
        sealedData.CopyTo(padded, 0);
        RandomNumberGenerator.Fill(padded.AsSpan(sealedData.Length));
        return padded;
    }

    public async Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)
    {
        SegmentManifest manifest;
        try { manifest = await ReadManifest(key, reference, cancellationToken); }
        catch (ServiceUnavailableException) { return; }
        await Task.WhenAll(manifest.Shards.Select(shard => directory.Discard(shard.NodeId, shard.Cid)));
        await Task.WhenAll(reference.ManifestNodes.Select(nodeId => directory.Discard(nodeId, reference.ManifestCid)));
    }

    // Put is idempotent on a node, so re-storing an unmoved shard only refreshes its challenges.
    private async Task<ShardLocation> Store(int index, string nodeId, byte[] bytes)
    {
        var cid = StorageProof.ContentId(bytes);
        await directory.Put(nodeId, cid, bytes);
        return new ShardLocation { Index = index, Cid = cid, NodeId = nodeId, Challenges = Challenges(bytes) };
    }

    private async Task<MeshRef> WriteManifest(byte[] key, string segmentId, SegmentManifest manifest, CancellationToken cancellationToken)
    {
        var sealedManifest = SegmentSeal.Close(key, ManifestContext(segmentId), serializer.SerializeToArray(manifest));
        var cid = StorageProof.ContentId(sealedManifest);
        var replicas = await Directory.Place(options.Value.ManifestReplicas, [], sealedManifest.Length);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(replicas.Select(nodeId => directory.Put(nodeId, cid, sealedManifest)));
        return new MeshRef(segmentId, cid, replicas);
    }

    // The first replica whose bytes hash to the ref's cid; the seal then proves the key and the segment id.
    private async Task<SegmentManifest> ReadManifest(byte[] key, MeshRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        foreach (var nodeId in reference.ManifestNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await directory.Get(nodeId, reference.ManifestCid) is { } bytes && StorageProof.ContentId(bytes) == reference.ManifestCid)
            {
                var manifest = serializer.Deserialize<SegmentManifest>(SegmentSeal.Open(key, ManifestContext(reference.SegmentId), bytes))
                    ?? throw new InvalidDataException("Manifest is empty.");
                if (manifest.Shards.Where((shard, i) => shard.Index != i).Any()) { throw new InvalidDataException("Manifest shards are out of order."); }
                return manifest;
            }
        }
        throw new ServiceUnavailableException($"No replica of the manifest for segment '{reference.SegmentId}' is readable.");
    }

    private async Task<Dictionary<string, StorageNodeView>> Health() =>
        (await Directory.List()).ToDictionary(node => node.NodeId, StringComparer.Ordinal);

    // Nodes known to be offline are not asked. A shard whose bytes do not hash to its cid is an erasure, never data.
    private async Task<(byte[]?[] Shards, int[] Unreadable)> Fetch(SegmentManifest manifest, Dictionary<string, StorageNodeView> health)
    {
        var fetched = await Task.WhenAll(manifest.Shards.Select(async location =>
            health.TryGetValue(location.NodeId, out var node) && node.Online
                ? await directory.Get(location.NodeId, location.Cid) is { } bytes && StorageProof.ContentId(bytes) == location.Cid ? bytes : null
                : null));
        return (fetched, [.. Enumerable.Range(0, fetched.Length).Where(i => fetched[i] is null)]);
    }

    private ShardChallenge[] Challenges(byte[] shard) =>
        [.. Enumerable.Range(0, options.Value.ChallengesPerShard).Select(_ =>
        {
            var nonce = RandomNumberGenerator.GetBytes(16);
            return new ShardChallenge { Nonce = nonce, Expected = StorageProof.Compute(nonce, shard) };
        })];

    private static string DataContext(string segmentId) => $"mesh-data|{segmentId}";

    private static string ManifestContext(string segmentId) => $"mesh-manifest|{segmentId}";
}

[GenerateSerializer, Alias("platform.mesh.manifest")]
internal sealed class SegmentManifest
{
    [Id(0)] public int DataShards { get; set; }
    [Id(1)] public int ParityShards { get; set; }
    [Id(2)] public int ShardSize { get; set; }
    [Id(3)] public int SealedLength { get; set; }
    [Id(4)] public ShardLocation[] Shards { get; set; } = [];
}

[GenerateSerializer, Alias("platform.mesh.shard-location")]
internal sealed class ShardLocation
{
    [Id(0)] public int Index { get; set; }
    [Id(1)] public string Cid { get; set; } = "";
    [Id(2)] public string NodeId { get; set; } = "";
    [Id(3)] public ShardChallenge[] Challenges { get; set; } = [];
}

[GenerateSerializer, Alias("platform.mesh.shard-challenge")]
internal sealed class ShardChallenge
{
    [Id(0)] public byte[] Nonce { get; set; } = [];
    [Id(1)] public byte[] Expected { get; set; } = [];
}
