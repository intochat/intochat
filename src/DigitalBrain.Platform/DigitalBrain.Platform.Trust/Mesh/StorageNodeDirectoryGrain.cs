using DigitalBrain.Framework;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Mesh;

// The network's storage nodes, their owners and audit reputation: one directory for every brain, beside the
// sequencer. A plain grain, not a neuron, so no script can register nodes or steer placement.
internal interface IStorageNodeDirectory : IGrainWithStringKey
{
    const string Key = "mesh";

    // R75: minted on first ask and kept in platform storage: one per trust deployment.
    Task<string> DeploymentId();

    // Seeds and facts only (trust-internal, T23-5): a node without a machine.
    Task Register(string nodeId, string ownerAccount);

    Task SetOnline(string nodeId, bool online);

    // Trust-internal reputation change; a machine reports through Report.
    Task ReportAudit(string nodeId, bool passed);

    // T23-5: a volunteer node is registered by its own machine, signed, for an account the machine serves.
    Task RegisterNode(MachineSigned<NodeRegistration> registration);

    // T23-5: only the node's own machine beats for it; LastSeen decides Online at read.
    Task Heartbeat(string machineId, string nodeId, long usedBytes);

    // T23-10/R112: from an account placed onto the node, never one of its owner's; null accounts = this silo's own
    // machine (Stage A), which has nobody to be bound against. Returns the report as recorded (ShardBytes clamped).
    Task<AuditReport> Report(AuditReport report, string[]? reporterAccounts);

    // T23-6: count healthy online nodes with room for shardBytes, never one of the asking accounts' own; volunteers one
    // per owner first, then seeds (the platform as one owner), then the rest. The owners of `exclude` (a segment's
    // current holders) are avoided the same way, so a repair does not land beside a sibling shard. Every node returned
    // records the asking accounts (PlacedFor, R112/R117).
    Task<string[]> Place(int count, string[] exclude, int shardBytes, string[] askingAccounts);

    [AlwaysInterleave]
    Task<StorageNodeView[]> List();
}

[GenerateSerializer, Alias("platform.mesh.node-view")]
internal sealed record StorageNodeView(
    [property: Id(0)] string NodeId,
    [property: Id(1)] string OwnerAccount,
    [property: Id(2)] bool Online,
    [property: Id(3)] double Reputation,
    [property: Id(4)] string? MachineId = null,
    [property: Id(5)] long OfferedBytes = 0,
    [property: Id(6)] long UsedBytes = 0,
    [property: Id(7)] DateTimeOffset LastSeen = default)
{
    public const double Healthy = 0.25;

    public bool Usable => Online && Reputation >= Healthy;

    public bool Volunteer => MachineId is not null;

    public bool HasRoom(long shardBytes) => !Volunteer || OfferedBytes - UsedBytes >= shardBytes;
}

[GenerateSerializer, Alias("platform.mesh.node-registration")]
internal sealed record NodeRegistration(
    [property: Id(0)] string NodeId,
    [property: Id(1)] string MachineId,
    [property: Id(2)] string OwnerAccount,
    [property: Id(3)] long OfferedBytes);

// SegmentCid is the manifest's content id: no name crosses (Q23).
[GenerateSerializer, Alias("platform.mesh.audit-report")]
internal sealed record AuditReport(
    [property: Id(0)] string NodeId,
    [property: Id(1)] string SegmentCid,
    [property: Id(2)] int Round,
    [property: Id(3)] bool Passed,
    [property: Id(4)] int ShardBytes);

// T23-4: a node refuses a put beyond its offer or the putter account's share; crosses links by its type name.
[GenerateSerializer, Alias("platform.mesh.node-full")]
internal sealed class NodeFullException(string message) : Exception(message);

internal static class StorageNodes
{
    // T23-1: a volunteer node is its machine's, so which link serves it cannot be lied about.
    public static string IdOf(string machineId) => $"node/{machineId}";

    public const int MaxPlacedAccounts = 64;
}

[GrainType("platform.mesh.directory")]
internal sealed class StorageNodeDirectoryGrain(
    [PersistentState("mesh-directory", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<StorageNodeDirectoryState> store,
    [FromKeyedServices(MeshOptions.Key)] TimeProvider time, IOptions<MeshOptions> options)
    : Grain, IStorageNodeDirectory
{
    public async Task<string> DeploymentId()
    {
        if (store.State.DeploymentId is { Length: > 0 } id) { return id; }
        store.State.DeploymentId = Guid.NewGuid().ToString("N");
        await store.WriteStateAsync();
        return store.State.DeploymentId;
    }

    public async Task Register(string nodeId, string ownerAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAccount);
        var existing = store.State.Nodes.GetValueOrDefault(nodeId);
        store.State.Nodes[nodeId] = existing is null ? new StorageNodeView(nodeId, ownerAccount, true, 1.0) : existing with { OwnerAccount = ownerAccount };
        await store.WriteStateAsync();
    }

    public Task SetOnline(string nodeId, bool online) => Update(nodeId, node => node with { Online = online });

    public Task ReportAudit(string nodeId, bool passed) => Update(nodeId, node => Reputed(node, passed));

    public async Task RegisterNode(MachineSigned<NodeRegistration> registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var node = registration.Payload;
        // Signature, binding of the signed account, timestamp and counter (R52/R55).
        await GrainFactory.GetGrain<IMachineRegistry>(registration.MachineId).Accept(registration.Call());
        if (node.MachineId != registration.MachineId || node.NodeId != StorageNodes.IdOf(node.MachineId) || node.OwnerAccount != registration.Account)
        { throw new UnauthorizedAccessException($"Machine {registration.MachineId} may register only its own node, for an account it signs for."); }
        Reserved.Refuse(node.OwnerAccount);
        ArgumentOutOfRangeException.ThrowIfNegative(node.OfferedBytes);
        var existing = store.State.Nodes.GetValueOrDefault(node.NodeId);
        if (existing is not null && existing.MachineId != node.MachineId)
        { throw new UnauthorizedAccessException($"Node {node.NodeId} is not machine {node.MachineId}'s."); }
        store.State.Nodes[node.NodeId] = new StorageNodeView(node.NodeId, node.OwnerAccount, true, existing?.Reputation ?? 1.0,
            node.MachineId, node.OfferedBytes, existing?.UsedBytes ?? 0, time.GetUtcNow());
        await store.WriteStateAsync();
    }

    public async Task Heartbeat(string machineId, string nodeId, long usedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);
        if (!store.State.Nodes.TryGetValue(nodeId, out var node)) { throw new KeyNotFoundException($"Storage node '{nodeId}' is not registered."); }
        if (node.MachineId is null || node.MachineId != machineId)
        { throw new UnauthorizedAccessException($"Machine {machineId} does not run node {nodeId}."); }
        store.State.Nodes[nodeId] = node with { Online = true, UsedBytes = usedBytes, LastSeen = time.GetUtcNow() };
        await store.WriteStateAsync();
    }

    public async Task<AuditReport> Report(AuditReport report, string[]? reporterAccounts)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!store.State.Nodes.TryGetValue(report.NodeId, out var node)) { throw new KeyNotFoundException($"Storage node '{report.NodeId}' is not registered."); }
        if (reporterAccounts is not null)
        {
            if (reporterAccounts.Contains(node.OwnerAccount, StringComparer.Ordinal))
            { throw new UnauthorizedAccessException($"The owner of node {report.NodeId} cannot report on it."); }
            if (!store.State.PlacedFor.TryGetValue(report.NodeId, out var placed) || !placed.Intersect(reporterAccounts, StringComparer.Ordinal).Any())
            { throw new UnauthorizedAccessException($"No account of the reporting machine was placed onto node {report.NodeId}."); }
        }
        var recorded = report with { ShardBytes = Math.Clamp(report.ShardBytes, 0, options.Value.MaxShardBytes) };
        store.State.Nodes[report.NodeId] = Reputed(node, report.Passed);
        await store.WriteStateAsync();
        return recorded;
    }

    public async Task<string[]> Place(int count, string[] exclude, int shardBytes, string[] askingAccounts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var askers = new HashSet<string>(askingAccounts ?? [], StringComparer.Ordinal);
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.Ordinal);
        var nodes = Current();
        var taken = nodes.Where(node => excluded.Contains(node.NodeId)).Select(node => node.OwnerAccount);
        var candidates = nodes
            .Where(node => node.Usable && !excluded.Contains(node.NodeId) && !askers.Contains(node.OwnerAccount) && node.HasRoom(shardBytes))
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();

        var chosen = new List<StorageNodeView>();
        var owners = new HashSet<string>(taken, StringComparer.Ordinal);
        foreach (var node in candidates.Where(node => node.Volunteer).Concat(candidates.Where(node => !node.Volunteer)))
        { if (chosen.Count < count && owners.Add(node.OwnerAccount)) { chosen.Add(node); } }
        foreach (var node in candidates) { if (chosen.Count < count && !chosen.Contains(node)) { chosen.Add(node); } }

        if (chosen.Count < count) { throw new ServiceUnavailableException($"Mesh needs {count} healthy storage nodes; {chosen.Count} are available."); }
        if (askers.Count > 0)
        {
            foreach (var node in chosen)
            {
                var placed = store.State.PlacedFor.GetValueOrDefault(node.NodeId) ?? [];
                store.State.PlacedFor[node.NodeId] = [.. placed.Where(account => !askers.Contains(account)).Concat(askers).TakeLast(StorageNodes.MaxPlacedAccounts)];
            }
            await store.WriteStateAsync();
        }
        return [.. chosen.Select(node => node.NodeId)];
    }

    public Task<StorageNodeView[]> List() => Task.FromResult(Current());

    // T23-5: a volunteer is online while its heartbeats are recent; computed at read, no timer.
    private StorageNodeView[] Current()
    {
        var since = time.GetUtcNow() - options.Value.OfflineAfter;
        return [.. store.State.Nodes.Values.Select(node => node.Volunteer && node.LastSeen <= since ? node with { Online = false } : node)];
    }

    private static StorageNodeView Reputed(StorageNodeView node, bool passed) =>
        node with { Reputation = passed ? Math.Min(1, node.Reputation + 0.02) : node.Reputation * 0.5 };

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

    [Id(1)] public string DeploymentId { get; set; } = "";

    // R112/R117: per node, the accounts Place returned it to (newest last, at most MaxPlacedAccounts).
    [Id(2)] public Dictionary<string, string[]> PlacedFor { get; set; } = new(StringComparer.Ordinal);
}
