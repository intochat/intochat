using DigitalBrain.Framework;

namespace DigitalBrain.Mesh;

// A seed node: shards persist in this neuron's grain storage. Volunteer nodes implement the same contract on
// their own machine's silo in a later plan.
[GrainType("mesh.node")]
internal sealed class StorageNodeNeuron(
    [PersistentState("mesh.node", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<StorageNodeState> store) : Neuron, IStorageNode
{
    public async Task Put(string cid, byte[] shard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shard);
        if (StorageProof.ContentId(shard) != cid) { throw new InvalidDataException($"Shard bytes do not match content id '{cid}'."); }
        store.State.Shards[cid] = shard;
        await store.WriteStateAsync(cancellationToken);
    }

    public Task<byte[]?> Get(string cid, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.State.Shards.GetValueOrDefault(cid));

    public async Task Delete(string cid, CancellationToken cancellationToken = default)
    {
        if (store.State.Shards.Remove(cid)) { await store.WriteStateAsync(cancellationToken); }
    }

    public Task<byte[]?> Prove(string cid, byte[] nonce, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.State.Shards.TryGetValue(cid, out var shard) ? StorageProof.Compute(nonce, shard) : null);

    public Task<long> BytesStored(CancellationToken cancellationToken = default) =>
        Task.FromResult(store.State.Shards.Values.Sum(shard => (long)shard.Length));
}

[GenerateSerializer, Alias("mesh.node-state")]
internal sealed class StorageNodeState
{
    [Id(0)] public Dictionary<string, byte[]> Shards { get; set; } = new(StringComparer.Ordinal);
}
