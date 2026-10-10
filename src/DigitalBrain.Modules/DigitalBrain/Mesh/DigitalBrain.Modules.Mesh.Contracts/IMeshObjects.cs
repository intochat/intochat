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
