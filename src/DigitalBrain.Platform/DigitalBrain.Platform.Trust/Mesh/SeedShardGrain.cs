using DigitalBrain.Framework;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Mesh;

// One shard on one platform seed node (Q19), keyed "{nodeId}/{cid}". Shards are immutable: a put of bytes that do
// not hash to the cid is refused, and a repeated put is a no-op. Volunteer nodes answer the same three calls over
// the transport (plan 7); unreachable segments are reclaimed by reachability from log heads, never by refcounts.
internal interface IStorageShard : IGrainWithStringKey
{
    // Every machine that put the shard holds a claim on it (T20-8); a repeated put of the same bytes adds a claim,
    // so a cid cannot be squatted by putting it first.
    Task Put(byte[] bytes, string? putter);

    [AlwaysInterleave]
    Task<string[]> Putters();

    // Drops these machines' claims; the bytes go with the last claim.
    Task Forget(string[] putters);

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
        if (store.State.Bytes is not null && (putter is null || store.State.Putters.Contains(putter, StringComparer.Ordinal))) { return; }
        store.State.Bytes ??= bytes;
        if (putter is not null) { store.State.Putters = [.. store.State.Putters, putter]; }
        await store.WriteStateAsync();
    }

    public Task<string[]> Putters() => Task.FromResult<string[]>([.. store.State.Putters]);

    public async Task Forget(string[] putters)
    {
        ArgumentNullException.ThrowIfNull(putters);
        if (store.State.Bytes is null) { return; }
        store.State.Putters = [.. store.State.Putters.Except(putters, StringComparer.Ordinal)];
        if (store.State.Putters.Length == 0) { await store.ClearStateAsync(); }
        else { await store.WriteStateAsync(); }
    }

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
    [Id(1)] public string[] Putters { get; set; } = [];
}
