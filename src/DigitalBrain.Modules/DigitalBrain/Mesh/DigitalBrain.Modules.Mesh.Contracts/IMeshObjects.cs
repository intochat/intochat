using System.ComponentModel;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace DigitalBrain.Mesh;

// Objects encrypted with the caller's key and scattered as erasure-coded shards. Manifests hold no plaintext and no key.
[Alias("mesh.objects"), Orleans.Metadata.DefaultGrainType("mesh.objects")]
public interface IMeshObjects : INeuron
{
    [Description("Encrypt an object with the given 32-byte key and store it as shards; returns the new version.")]
    Task<long> Put(string objectId, byte[] plaintext, byte[] key, CancellationToken cancellationToken = default);

    [Description("Gather any sufficient set of verified shards, rebuild and decrypt the object; null when unknown.")]
    Task<byte[]?> Get(string objectId, byte[] key, CancellationToken cancellationToken = default);

    [Description("Delete an object and its shards.")]
    Task Delete(string objectId, CancellationToken cancellationToken = default);

    [Description("Where an object's shards are: version, content ids and nodes.")]
    [ReadOnly, AlwaysInterleave]
    Task<ObjectManifestView?> Manifest(string objectId, CancellationToken cancellationToken = default);

    [Description("Challenge every shard holder with a fresh storage proof.")]
    Task<AuditResult[]> Audit(string objectId, CancellationToken cancellationToken = default);

    [Description("Rebuild shards that are missing, corrupt or on unhealthy nodes and move them to healthy nodes.")]
    Task<RepairResult> Repair(string objectId, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("mesh.manifest-view")]
public sealed record ObjectManifestView(
    [property: Id(0)] string ObjectId,
    [property: Id(1)] long Version,
    [property: Id(2)] ShardView[] Shards);

[GenerateSerializer, Alias("mesh.shard-view")]
public sealed record ShardView(
    [property: Id(0)] int Index,
    [property: Id(1)] string Cid,
    [property: Id(2)] string NodeId);

[GenerateSerializer, Alias("mesh.audit-result")]
public sealed record AuditResult(
    [property: Id(0)] int Index,
    [property: Id(1)] string NodeId,
    [property: Id(2)] bool Passed,
    [property: Id(3)] string Reason);

[GenerateSerializer, Alias("mesh.repair-result")]
public sealed record RepairResult([property: Id(0)] ShardMove[] Moved);

[GenerateSerializer, Alias("mesh.shard-move")]
public sealed record ShardMove(
    [property: Id(0)] int Index,
    [property: Id(1)] string FromNode,
    [property: Id(2)] string ToNode);
