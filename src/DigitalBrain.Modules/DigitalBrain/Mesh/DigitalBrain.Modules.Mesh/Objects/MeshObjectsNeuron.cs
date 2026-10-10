using System.Collections.Concurrent;
using System.Security.Cryptography;
using DigitalBrain.Framework;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Mesh;

[GrainType("mesh.objects")]
internal sealed partial class MeshObjectsNeuron(
    IOptions<MeshOptions> options,
    [PersistentState("mesh.objects", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<MeshObjectsState> store) : Neuron, IMeshObjects
{
    private string NodesKey => this.GetPrimaryKeyString()[..^"/objects".Length] + "/nodes";

    private IStorageNodes Nodes => GrainFactory.GetGrain<IStorageNodes>(NodesKey);

    private IStorageNode Node(string nodeId) => GrainFactory.GetGrain<IStorageNode>(MeshKeys.Node(NodesKey, nodeId));

    public async Task<long> Put(string objectId, byte[] plaintext, byte[] key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var previous = store.State.Objects.GetValueOrDefault(objectId);
        var version = (previous?.Version ?? 0) + 1;
        var rs = new ReedSolomon(options.Value.DataShards, options.Value.ParityShards);
        var nodeIds = await Nodes.Place(rs.TotalShards, [], cancellationToken);

        var cipher = ObjectSeal.Seal(key, objectId, version, plaintext, out var nonce);
        var shards = rs.Encode(cipher, out var shardSize);
        var locations = new ShardLocation[rs.TotalShards];
        await Task.WhenAll(Enumerable.Range(0, rs.TotalShards).Select(async i =>
        {
            var cid = StorageProof.ContentId(shards[i]);
            await Node(nodeIds[i]).Put(cid, shards[i], cancellationToken);
            locations[i] = new ShardLocation { Index = i, Cid = cid, NodeId = nodeIds[i], Challenges = Challenges(shards[i]) };
        }));

        store.State.Objects[objectId] = new ObjectManifest
        {
            Version = version, DataShards = rs.DataShards, ParityShards = rs.ParityShards, ShardSize = shardSize,
            CipherLength = cipher.Length, Nonce = nonce, Shards = locations,
        };
        await store.WriteStateAsync(cancellationToken);
        if (previous is not null) { await DeleteShards(previous, cancellationToken); }
        return version;
    }

    public async Task<byte[]?> Get(string objectId, byte[] key, CancellationToken cancellationToken = default)
    {
        if (!store.State.Objects.TryGetValue(objectId, out var manifest)) { return null; }
        var rs = new ReedSolomon(manifest.DataShards, manifest.ParityShards);
        var (shards, _) = await Fetch(manifest, cancellationToken);
        var cipher = rs.Join(rs.Reconstruct(shards, manifest.ShardSize), manifest.CipherLength);
        return ObjectSeal.Open(key, objectId, manifest.Version, manifest.Nonce, cipher);
    }

    public async Task Delete(string objectId, CancellationToken cancellationToken = default)
    {
        if (!store.State.Objects.Remove(objectId, out var manifest)) { return; }
        await store.WriteStateAsync(cancellationToken);
        await DeleteShards(manifest, cancellationToken);
    }

    public Task<ObjectManifestView?> Manifest(string objectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.State.Objects.TryGetValue(objectId, out var m)
            ? new ObjectManifestView(objectId, m.Version, m.Shards.Select(s => new ShardView(s.Index, s.Cid, s.NodeId)).ToArray())
            : null);

    // A shard whose bytes do not hash to its cid is an erasure, never data.
    private async Task<(byte[]?[] Shards, int[] Bad)> Fetch(ObjectManifest manifest, CancellationToken cancellationToken)
    {
        var shards = new byte[]?[manifest.Shards.Length];
        var bad = new ConcurrentBag<int>();
        await Task.WhenAll(manifest.Shards.Select(async location =>
        {
            var bytes = await Node(location.NodeId).Get(location.Cid, cancellationToken);
            if (bytes is null || StorageProof.ContentId(bytes) != location.Cid) { bad.Add(location.Index); }
            else { shards[location.Index] = bytes; }
        }));
        return (shards, bad.Order().ToArray());
    }

    private async Task DeleteShards(ObjectManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var shard in manifest.Shards) { await Node(shard.NodeId).Delete(shard.Cid, cancellationToken); }
    }

    private ShardChallenge[] Challenges(byte[] shard) =>
        Enumerable.Range(0, options.Value.ChallengesPerShard).Select(_ =>
        {
            var nonce = RandomNumberGenerator.GetBytes(16);
            return new ShardChallenge { Nonce = nonce, Expected = StorageProof.Compute(nonce, shard) };
        }).ToArray();
}

[GenerateSerializer, Alias("mesh.objects-state")]
internal sealed class MeshObjectsState
{
    [Id(0)] public Dictionary<string, ObjectManifest> Objects { get; set; } = new(StringComparer.Ordinal);
}

[GenerateSerializer, Alias("mesh.manifest")]
internal sealed class ObjectManifest
{
    [Id(0)] public long Version { get; set; }
    [Id(1)] public int DataShards { get; set; }
    [Id(2)] public int ParityShards { get; set; }
    [Id(3)] public int ShardSize { get; set; }
    [Id(4)] public int CipherLength { get; set; }
    [Id(5)] public byte[] Nonce { get; set; } = [];
    [Id(6)] public ShardLocation[] Shards { get; set; } = [];
}

[GenerateSerializer, Alias("mesh.shard-location")]
internal sealed class ShardLocation
{
    [Id(0)] public int Index { get; set; }
    [Id(1)] public string Cid { get; set; } = "";
    [Id(2)] public string NodeId { get; set; } = "";
    [Id(3)] public ShardChallenge[] Challenges { get; set; } = [];
    [Id(4)] public int NextChallenge { get; set; }
}

[GenerateSerializer, Alias("mesh.shard-challenge")]
internal sealed class ShardChallenge
{
    [Id(0)] public byte[] Nonce { get; set; } = [];
    [Id(1)] public byte[] Expected { get; set; } = [];
}
