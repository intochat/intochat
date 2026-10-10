using DigitalBrain.Framework;

namespace DigitalBrain.Mesh;

[GrainType("mesh.nodes")]
internal sealed class StorageNodesNeuron(
    [PersistentState("mesh.nodes", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<StorageNodesState> store) : Neuron, IStorageNodes
{
    private const double Healthy = 0.25;

    public async Task Register(string nodeId, string ownerAccount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAccount);
        var existing = store.State.Nodes.GetValueOrDefault(nodeId);
        store.State.Nodes[nodeId] = new StorageNodeView(nodeId, ownerAccount, true, existing?.Reputation ?? 1.0);
        await store.WriteStateAsync(cancellationToken);
    }

    public Task SetOnline(string nodeId, bool online, CancellationToken cancellationToken = default) =>
        Update(nodeId, node => node with { Online = online }, cancellationToken);

    public Task ReportAudit(string nodeId, bool passed, CancellationToken cancellationToken = default) =>
        Update(nodeId, node => node with { Reputation = passed ? Math.Min(1, node.Reputation + 0.02) : node.Reputation * 0.5 }, cancellationToken);

    public Task<string[]> Place(int count, string[] exclude, CancellationToken cancellationToken = default)
    {
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.Ordinal);
        var candidates = store.State.Nodes.Values
            .Where(node => node.Online && node.Reputation >= Healthy && !excluded.Contains(node.NodeId))
            .OrderByDescending(node => node.Reputation)
            .ThenBy(_ => Random.Shared.Next())
            .ToArray();

        var chosen = new List<StorageNodeView>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in candidates) { if (chosen.Count < count && owners.Add(node.OwnerAccount)) { chosen.Add(node); } }
        foreach (var node in candidates) { if (chosen.Count < count && !chosen.Contains(node)) { chosen.Add(node); } }

        if (chosen.Count < count) { throw new ServiceUnavailableException($"Mesh needs {count} healthy storage nodes; {chosen.Count} are available."); }
        return Task.FromResult(chosen.Select(node => node.NodeId).ToArray());
    }

    public Task<StorageNodeView[]> List(CancellationToken cancellationToken = default) =>
        Task.FromResult(store.State.Nodes.Values.ToArray());

    private async Task Update(string nodeId, Func<StorageNodeView, StorageNodeView> change, CancellationToken cancellationToken)
    {
        if (!store.State.Nodes.TryGetValue(nodeId, out var node)) { throw new KeyNotFoundException($"Storage node '{nodeId}' is not registered."); }
        store.State.Nodes[nodeId] = change(node);
        await store.WriteStateAsync(cancellationToken);
    }
}

[GenerateSerializer, Alias("mesh.nodes-state")]
internal sealed class StorageNodesState
{
    [Id(0)] public Dictionary<string, StorageNodeView> Nodes { get; set; } = new(StringComparer.Ordinal);
}
