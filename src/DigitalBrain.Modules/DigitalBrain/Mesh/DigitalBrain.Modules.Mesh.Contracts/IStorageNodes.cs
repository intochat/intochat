using System.ComponentModel;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace DigitalBrain.Mesh;

// The storage nodes this mesh can place shards on, with their owners and audit reputation.
[Alias("mesh.nodes"), Orleans.Metadata.DefaultGrainType("mesh.nodes")]
public interface IStorageNodes : INeuron
{
    [Description("Register a storage node and the account that owns it.")]
    Task Register(string nodeId, string ownerAccount, CancellationToken cancellationToken = default);

    [Description("Mark a storage node online or offline.")]
    Task SetOnline(string nodeId, bool online, CancellationToken cancellationToken = default);

    [Description("Record whether a node passed a storage proof.")]
    Task ReportAudit(string nodeId, bool passed, CancellationToken cancellationToken = default);

    [Description("Choose count healthy online nodes, distinct owners first; refuses when not enough exist.")]
    Task<string[]> Place(int count, string[] exclude, CancellationToken cancellationToken = default);

    [Description("List registered storage nodes.")]
    [ReadOnly, AlwaysInterleave]
    Task<StorageNodeView[]> List(CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("mesh.node-view")]
public sealed record StorageNodeView(
    [property: Id(0)] string NodeId,
    [property: Id(1)] string OwnerAccount,
    [property: Id(2)] bool Online,
    [property: Id(3)] double Reputation);

public static class MeshKeys
{
    public static string Node(string nodesKey, string nodeId) => $"{nodesKey}/{nodeId}";
}
