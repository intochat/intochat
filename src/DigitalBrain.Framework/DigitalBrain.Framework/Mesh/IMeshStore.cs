namespace DigitalBrain.Framework.Mesh;

// Immutable encrypted segments on storage nodes nobody has to trust. Sealing, erasure coding and the manifest
// all happen in the caller's own silo with the caller's key: nodes only ever see ciphertext and content ids.
// Nothing here is mutable: a segment is written once and named by its ref. Whoever must find it again (a part's
// log head, the sequencer record) keeps the ref; that is where versions and compare-and-set live.
public interface IMeshStore
{
    // segmentId is authenticated with the data, so a segment cannot be replayed as another one.
    Task<MeshRef> Write(byte[] key, string segmentId, byte[] plaintext, CancellationToken cancellationToken = default);

    // Gathers any sufficient set of verified shards; throws when the ref's manifest or data fail to authenticate.
    Task<byte[]> Read(byte[] key, MeshRef reference, CancellationToken cancellationToken = default);

    // Spends challenge number `round` on every shard. The caller counts rounds; once they run out, Repair re-issues.
    Task<ShardAudit[]> Audit(byte[] key, MeshRef reference, int round, CancellationToken cancellationToken = default);

    // Rebuilds shards that are missing, corrupt or on unhealthy nodes onto nodes of other owners, with fresh
    // challenges, as a new manifest. Returns the same ref when nothing needed moving and reissue is false.
    Task<MeshRepair> Repair(byte[] key, MeshRef reference, bool reissue = false, CancellationToken cancellationToken = default);

    // Drops a segment's shards and manifest replicas. Safe because segments never share a shard (random padding).
    Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default);
}

// The manifest is itself sealed and pinned by its content id, so a ref cannot be pointed at other shards.
[GenerateSerializer, Alias("mesh.ref")]
public sealed record MeshRef(
    [property: Id(0)] string SegmentId,
    [property: Id(1)] string ManifestCid,
    [property: Id(2)] string[] ManifestNodes);

[GenerateSerializer, Alias("mesh.shard-audit")]
public sealed record ShardAudit(
    [property: Id(0)] int Index,
    [property: Id(1)] string NodeId,
    [property: Id(2)] bool Passed,
    [property: Id(3)] string Reason);

[GenerateSerializer, Alias("mesh.repair")]
public sealed record MeshRepair(
    [property: Id(0)] MeshRef Reference,
    [property: Id(1)] ShardMove[] Moved);

[GenerateSerializer, Alias("mesh.shard-move")]
public sealed record ShardMove(
    [property: Id(0)] int Index,
    [property: Id(1)] string FromNode,
    [property: Id(2)] string ToNode);
