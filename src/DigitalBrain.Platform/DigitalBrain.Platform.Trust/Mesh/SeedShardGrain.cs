using DigitalBrain.Framework;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Mesh;

// One shard on one platform seed node (Q19), keyed "{nodeId}/{cid}". Shards are immutable: a put of bytes that do
// not hash to the cid is refused, and a repeated put is a no-op. Volunteer nodes answer the same three calls over
// the transport (plan 7); unreachable segments are reclaimed by reachability from log heads, never by refcounts.
internal interface IStorageShard : IGrainWithStringKey
{
    // The putter is the machine that stored the shard (T20-8): only a machine of the same account may discard it.
    Task Put(byte[] bytes, string? putter);

    [AlwaysInterleave]
    Task<string?> Putter();

    [AlwaysInterleave]
    Task<byte[]?> Get();

    [AlwaysInterleave]
    Task<byte[]?> Prove(byte[] nonce);

    // What a node that discards data does; the reachability sweep uses it too.
    Task Drop();
}

internal static class StorageShards
{
    public static string Key(string nodeId, string cid) => $"{nodeId}/{cid}";
}

[GrainType("platform.mesh.seed-shard")]
internal sealed class SeedShardGrain(
    [PersistentState("mesh-shard", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<SeedShardState> store)
    : Grain, IStorageShard
{
    private string Cid => this.GetPrimaryKeyString()[(this.GetPrimaryKeyString().IndexOf('/', StringComparison.Ordinal) + 1)..];

    public async Task Put(byte[] bytes, string? putter)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (StorageProof.ContentId(bytes) != Cid) { throw new InvalidDataException($"Shard bytes do not match content id '{Cid}'."); }
        if (store.State.Bytes is not null) { return; }
        store.State.Bytes = bytes;
        store.State.Putter = putter;
        await store.WriteStateAsync();
    }

    public Task<string?> Putter() => Task.FromResult(store.State.Putter);

    public Task<byte[]?> Get() => Task.FromResult(store.State.Bytes);

    public Task<byte[]?> Prove(byte[] nonce) =>
        Task.FromResult(store.State.Bytes is { } bytes ? StorageProof.Compute(nonce, bytes) : null);

    public async Task Drop()
    {
        if (store.State.Bytes is null) { return; }
        await store.ClearStateAsync();
    }
}

[GenerateSerializer, Alias("platform.mesh.seed-shard-state")]
internal sealed class SeedShardState
{
    [Id(0)] public byte[]? Bytes { get; set; }
    [Id(1)] public string? Putter { get; set; }
}
