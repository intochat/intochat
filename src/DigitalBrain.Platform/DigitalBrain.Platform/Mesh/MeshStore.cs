using System.Security.Cryptography;
using DigitalBrain.Framework.Mesh;
using Microsoft.Extensions.Options;
using DigitalBrain.Platform.Trust;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Mesh;

// Runs in the caller's silo: the key and the plaintext never leave it. A segment is sealed, erasure-coded and
// scattered; its manifest (shard cids, nodes and challenges) is sealed with the same key and replicated, and the
// ref pins the manifest by content id. Writes create, they never overwrite, so a failure leaves only unreachable
// shards for the reachability sweep, never a half-changed segment. Shards go to seed or volunteer nodes through
// IShardClient; a node that is full or unreachable at write time is a placement miss, placed once more (T23-4, R119).
internal sealed class MeshStore(INodeDirectoryClient directory, IShardClient shards, Serializer serializer, IOptions<MeshOptions> options) : IMeshStore
{
    private const int MinShardSize = 16;

    private INodeDirectoryClient Directory => directory;

    public async Task<MeshRef> Write(byte[] key, string segmentId, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var settings = options.Value;
        var rs = new ReedSolomon(settings.DataShards, settings.ParityShards);
        var sealedData = SegmentSeal.Close(key, DataContext(segmentId), plaintext);
        var encoded = rs.Encode(Padded(sealedData, rs.DataShards), out var shardSize);
        // T23-3: a node refuses a larger shard; a segment over DataShards x MaxShardBytes is refused here.
        if (shardSize > settings.MaxShardBytes)
        { throw new InvalidOperationException($"Segment '{segmentId}' needs {shardSize}-byte shards; the mesh stores at most {settings.MaxShardBytes} bytes per shard."); }
        var placement = await Directory.Place(rs.TotalShards, [], shardSize);
        var nodes = placement.Nodes;
        cancellationToken.ThrowIfCancellationRequested();

        var locations = await Task.WhenAll(Enumerable.Range(0, rs.TotalShards).Select(i => Store(i, nodes[i], placement.Id, encoded[i], nodes, cancellationToken)));
        var manifest = new SegmentManifest
        {
            DataShards = rs.DataShards, ParityShards = rs.ParityShards, ShardSize = shardSize, SealedLength = sealedData.Length, Shards = locations,
        };
        return await WriteManifest(key, segmentId, manifest, cancellationToken);
    }

    public async Task<byte[]> Read(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifest(key, reference, cancellationToken);
        var (fetched, _) = await Fetch(manifest, await Health(), cancellationToken);
        var rs = new ReedSolomon(manifest.DataShards, manifest.ParityShards);
        var sealedData = rs.Join(rs.Reconstruct(fetched, manifest.ShardSize), manifest.SealedLength);
        return SegmentSeal.Open(key, DataContext(reference.SegmentId), sealedData);
    }

    public async Task<ShardAudit[]> Audit(byte[] key, MeshRef reference, int round, CancellationToken cancellationToken = default)
    {
        var (results, shardSize, placements) = await Prove(key, reference, round, cancellationToken);
        return await Report(reference, round, shardSize, results, placements);
    }

    // Spends challenge `round` on every shard without telling anyone (the holder reports only refs its head still
    // reaches, R7/T23-9). "challenges exhausted" is judged by the manifest's own challenges (R110).
    internal async Task<(ShardAudit[] Results, int ShardSize, string?[] Placements)> Prove(byte[] key, MeshRef reference, int round, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(round);
        var manifest = await ReadManifest(key, reference, cancellationToken);
        var health = await Health();
        var results = await Task.WhenAll(manifest.Shards.Select(async shard =>
        {
            if (round >= shard.Challenges.Length) { return new ShardAudit(shard.Index, shard.NodeId, false, Exhausted); }
            // A node the directory knows is offline is not asked and not reported; repair moves its shard (T23-9).
            if (!(health.TryGetValue(shard.NodeId, out var node) && node.Online)) { return new ShardAudit(shard.Index, shard.NodeId, false, Offline); }
            var challenge = shard.Challenges[round];
            var proof = await shards.Prove(shard.NodeId, shard.Cid, challenge.Nonce, cancellationToken);
            var passed = proof is not null && CryptographicOperations.FixedTimeEquals(proof, challenge.Expected);
            return new ShardAudit(shard.Index, shard.NodeId, passed, proof is null ? "shard missing" : passed ? "ok" : "wrong proof");
        }));
        return (results, manifest.ShardSize, [.. manifest.Shards.Select(shard => shard.PlacementId)]);
    }

    // T23-10: the segment is named by its manifest's content id. R117/R123: a report refused because this account's
    // placement on the node was evicted becomes "placement evicted": a re-place, not a node failure.
    // R142: each report names the placement its shard was stored under (`placements` by shard index).
    internal async Task<ShardAudit[]> Report(MeshRef reference, int round, int shardSize, ShardAudit[] results, string?[] placements)
        => await Task.WhenAll(results.Select(async result =>
        {
            if (result.Reason is Exhausted or Offline) { return result; }
            try { await Directory.ReportAudit(new AuditReport(result.NodeId, reference.ManifestCid, round, result.Passed, shardSize, placements.ElementAtOrDefault(result.Index))); }
            catch (UnauthorizedAccessException) { return result with { Passed = false, Reason = Evicted }; }
            return result;
        }));

    internal const string Exhausted = "challenges exhausted";

    internal const string Offline = "node offline";

    internal const string Evicted = "placement evicted";

    public Task<MeshRepair> Repair(byte[] key, MeshRef reference, bool reissue = false, CancellationToken cancellationToken = default)
        => Repair(key, reference, [], reissue, cancellationToken);

    // `replace`: indices to move although their node is healthy (an evicted placement, R123).
    internal async Task<MeshRepair> Repair(byte[] key, MeshRef reference, int[] replace, bool reissue, CancellationToken cancellationToken)
    {
        var manifest = await ReadManifest(key, reference, cancellationToken);
        var health = await Health();
        var (fetched, unreadable) = await Fetch(manifest, health, cancellationToken);
        var bad = unreadable
            .Concat(manifest.Shards.Where(s => !health.TryGetValue(s.NodeId, out var node) || !node.Usable).Select(s => s.Index))
            .Concat(replace)
            .Distinct().Order().ToArray();
        if (bad.Length == 0 && !reissue) { return new MeshRepair(reference, []); }

        var rs = new ReedSolomon(manifest.DataShards, manifest.ParityShards);
        var full = rs.Reconstruct(fetched, manifest.ShardSize);
        var targets = bad.Length == 0 ? new Placement("", []) : await Directory.Place(bad.Length, [.. manifest.Shards.Select(s => s.NodeId)], manifest.ShardSize);
        var moved = bad.Select((index, i) => new ShardMove(index, manifest.Shards[index].NodeId, targets.Nodes[i])).ToArray();
        var nodeOf = manifest.Shards.Select(s => s.NodeId).ToArray();
        var placementOf = manifest.Shards.Select(s => s.PlacementId).ToArray();
        foreach (var move in moved) { (nodeOf[move.Index], placementOf[move.Index]) = (move.ToNode, targets.Id); }

        var locations = await Task.WhenAll(Enumerable.Range(0, rs.TotalShards).Select(i => Store(i, nodeOf[i], placementOf[i], full[i], nodeOf, cancellationToken)));
        var repaired = new SegmentManifest
        {
            DataShards = manifest.DataShards, ParityShards = manifest.ParityShards, ShardSize = manifest.ShardSize,
            SealedLength = manifest.SealedLength, Shards = locations,
        };
        return new MeshRepair(await WriteManifest(key, reference.SegmentId, repaired, cancellationToken), moved);
    }

    // A ref's shard content ids by index: what the auditor drops from a moved shard's old node (T23-9).
    internal async Task<string[]> ShardCids(byte[] key, MeshRef reference, CancellationToken cancellationToken)
        => [.. (await ReadManifest(key, reference, cancellationToken)).Shards.Select(shard => shard.Cid)];

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
        await Task.WhenAll(manifest.Shards.Select(shard => shards.Drop(shard.NodeId, shard.Cid, cancellationToken)));
        await Task.WhenAll(reference.ManifestNodes.Select(nodeId => shards.Drop(nodeId, reference.ManifestCid, cancellationToken)));
    }

    // Put is idempotent on a node, so re-storing an unmoved shard only refreshes its challenges.
    private async Task<ShardLocation> Store(int index, string nodeId, string? placementId, byte[] bytes, string[] siblings, CancellationToken cancellationToken)
    {
        var cid = StorageProof.ContentId(bytes);
        var (stored, placement) = await Put(nodeId, cid, bytes, siblings, cancellationToken);
        return new ShardLocation { Index = index, Cid = cid, NodeId = stored, PlacementId = placement ?? placementId, Challenges = Challenges(bytes) };
    }

    // T23-4/R115/R119: a full or unreachable node is a placement miss: one more node, away from the segment's other
    // holders, then the write fails.
    // Returns the node stored on and, when it was placed again, that placement's id.
    private async Task<(string NodeId, string? PlacementId)> Put(string nodeId, string cid, byte[] bytes, string[] siblings, CancellationToken cancellationToken)
    {
        try
        {
            await shards.Put(nodeId, cid, bytes, cancellationToken);
            return (nodeId, null);
        }
        catch (Exception missed) when (missed is NodeFullException or ServiceUnavailableException)
        {
            var again = await Directory.Place(1, [nodeId, .. siblings.Where(sibling => sibling != nodeId)], bytes.Length);
            var other = again.Nodes[0];
            try { await shards.Put(other, cid, bytes, cancellationToken); }
            catch (Exception failed) when (failed is NodeFullException or ServiceUnavailableException)
            { throw new ServiceUnavailableException($"Shard {cid} found no node with room: {missed.Message}; then {failed.Message}"); }
            return (other, again.Id);
        }
    }

    private async Task<MeshRef> WriteManifest(byte[] key, string segmentId, SegmentManifest manifest, CancellationToken cancellationToken)
    {
        var sealedManifest = SegmentSeal.Close(key, ManifestContext(segmentId), serializer.SerializeToArray(manifest));
        var cid = StorageProof.ContentId(sealedManifest);
        var placed = (await Directory.Place(options.Value.ManifestReplicas, [], sealedManifest.Length)).Nodes;
        cancellationToken.ThrowIfCancellationRequested();
        var replicas = await Task.WhenAll(placed.Select(async nodeId => (await Put(nodeId, cid, sealedManifest, placed, cancellationToken)).NodeId));
        return new MeshRef(segmentId, cid, replicas);
    }

    // The first replica whose bytes hash to the ref's cid; the seal then proves the key and the segment id.
    private async Task<SegmentManifest> ReadManifest(byte[] key, MeshRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        foreach (var nodeId in reference.ManifestNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await shards.Get(nodeId, reference.ManifestCid, cancellationToken) is { } bytes && StorageProof.ContentId(bytes) == reference.ManifestCid)
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
    private async Task<(byte[]?[] Shards, int[] Unreadable)> Fetch(SegmentManifest manifest, Dictionary<string, StorageNodeView> health, CancellationToken cancellationToken)
    {
        var fetched = await Task.WhenAll(manifest.Shards.Select(async location =>
            health.TryGetValue(location.NodeId, out var node) && node.Online
                ? await shards.Get(location.NodeId, location.Cid, cancellationToken) is { } bytes && StorageProof.ContentId(bytes) == location.Cid ? bytes : null
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
    // R142: the placement the shard was stored under, named in its audit reports.
    [Id(4)] public string? PlacementId { get; set; }
}

[GenerateSerializer, Alias("platform.mesh.shard-challenge")]
internal sealed class ShardChallenge
{
    [Id(0)] public byte[] Nonce { get; set; } = [];
    [Id(1)] public byte[] Expected { get; set; } = [];
}
