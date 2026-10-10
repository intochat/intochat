using System.ComponentModel;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace DigitalBrain.Mesh;

// A storage machine nobody has to trust: it holds content-addressed shards of ciphertext and answers proofs.
[Alias("mesh.node"), Orleans.Metadata.DefaultGrainType("mesh.node")]
public interface IStorageNode : INeuron
{
    [Description("Store a shard under its content id.")]
    Task Put(string cid, byte[] shard, CancellationToken cancellationToken = default);

    [Description("Return a stored shard, or null when this node does not hold it.")]
    [ReadOnly, AlwaysInterleave]
    Task<byte[]?> Get(string cid, CancellationToken cancellationToken = default);

    [Description("Drop a shard.")]
    Task Delete(string cid, CancellationToken cancellationToken = default);

    [Description("Prove a shard is held: SHA-256 of the nonce followed by the shard, or null when it is not.")]
    [ReadOnly, AlwaysInterleave]
    Task<byte[]?> Prove(string cid, byte[] nonce, CancellationToken cancellationToken = default);

    [Description("Total bytes of shards held.")]
    [ReadOnly, AlwaysInterleave]
    Task<long> BytesStored(CancellationToken cancellationToken = default);
}
