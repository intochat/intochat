using DigitalBrain.Framework;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Mesh;

// The network's storage nodes, their owners and audit reputation: one directory for every brain, beside the
// sequencer. A plain grain, not a neuron, so no script can register nodes or steer placement.
internal interface IStorageNodeDirectory : IGrainWithStringKey
{
    const string Key = "mesh";

    Task Register(string nodeId, string ownerAccount);

    Task SetOnline(string nodeId, bool online);

    Task ReportAudit(string nodeId, bool passed);

    // count healthy online nodes, each owner at most once while enough owners exist. The owners of `exclude`
    // (a segment's current holders) are avoided the same way, so a repair does not land beside a sibling shard.
    Task<string[]> Place(int count, string[] exclude);

    [AlwaysInterleave]
    Task<StorageNodeView[]> List();
}

[GenerateSerializer, Alias("platform.mesh.node-view")]
internal sealed record StorageNodeView(
    [property: Id(0)] string NodeId,
    [property: Id(1)] string OwnerAccount,
    [property: Id(2)] bool Online,
    [property: Id(3)] double Reputation)
{
    public const double Healthy = 0.25;

    public bool Usable => Online && Reputation >= Healthy;
}

[GrainType("platform.mesh.directory")]
internal sealed class StorageNodeDirectoryGrain(
    [PersistentState("mesh-directory", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<StorageNodeDirectoryState> store)
    : Grain, IStorageNodeDirectory
{
    public async Task Register(string nodeId, string ownerAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAccount);
        var existing = store.State.Nodes.GetValueOrDefault(nodeId);
        store.State.Nodes[nodeId] = existing is null ? new StorageNodeView(nodeId, ownerAccount, true, 1.0) : existing with { OwnerAccount = ownerAccount };
        await store.WriteStateAsync();
    }

    public Task SetOnline(string nodeId, bool online) => Update(nodeId, node => node with { Online = online });

    public Task ReportAudit(string nodeId, bool passed) =>
        Update(nodeId, node => node with { Reputation = passed ? Math.Min(1, node.Reputation + 0.02) : node.Reputation * 0.5 });

    public Task<string[]> Place(int count, string[] exclude)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.Ordinal);
        var taken = new HashSet<string>(store.State.Nodes.Values.Where(node => excluded.Contains(node.NodeId)).Select(node => node.OwnerAccount), StringComparer.Ordinal);
        var candidates = store.State.Nodes.Values
            .Where(node => node.Usable && !excluded.Contains(node.NodeId))
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();

        var chosen = new List<StorageNodeView>();
        var owners = new HashSet<string>(taken, StringComparer.Ordinal);
        foreach (var node in candidates) { if (chosen.Count < count && owners.Add(node.OwnerAccount)) { chosen.Add(node); } }
        foreach (var node in candidates) { if (chosen.Count < count && !chosen.Contains(node)) { chosen.Add(node); } }

        if (chosen.Count < count) { throw new ServiceUnavailableException($"Mesh needs {count} healthy storage nodes; {chosen.Count} are available."); }
        return Task.FromResult(chosen.Select(node => node.NodeId).ToArray());
    }

    public Task<StorageNodeView[]> List() => Task.FromResult(store.State.Nodes.Values.ToArray());

    private async Task Update(string nodeId, Func<StorageNodeView, StorageNodeView> change)
    {
        if (!store.State.Nodes.TryGetValue(nodeId, out var node)) { throw new KeyNotFoundException($"Storage node '{nodeId}' is not registered."); }
        store.State.Nodes[nodeId] = change(node);
        await store.WriteStateAsync();
    }
}

[GenerateSerializer, Alias("platform.mesh.directory-state")]
internal sealed class StorageNodeDirectoryState
{
    [Id(0)] public Dictionary<string, StorageNodeView> Nodes { get; set; } = new(StringComparer.Ordinal);
}
