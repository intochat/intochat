using System.Diagnostics;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Trust;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Mesh;

// Where MeshStore's shards go: a seed node through the trust client's shard calls (T20-8), a volunteer node over a link to
// its own machine (T23-1: node/{machine}, so the link that serves a node is a pure function of its id).
internal interface IShardClient
{
    // NodeFullException, InvalidDataException; ServiceUnavailableException when the node cannot be reached (R119).
    Task Put(string nodeId, string cid, byte[] bytes, CancellationToken cancellationToken);

    // null when absent or unreachable.
    Task<byte[]?> Get(string nodeId, string cid, CancellationToken cancellationToken);

    Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce, CancellationToken cancellationToken);

    Task Drop(string nodeId, string cid, CancellationToken cancellationToken);
}

// A host without machine links (Stage A, a single host) reaches seeds only: a volunteer is unreachable from it.
internal sealed class ShardClient(INodeDirectoryClient directory, Serializer serializer, IMachineTransport? transport = null) : IShardClient
{
    public static readonly ActivitySource Source = new("DigitalBrain.Mesh.Node");

    private const string Prefix = "node/";

    public async Task Put(string nodeId, string cid, byte[] bytes, CancellationToken cancellationToken)
    {
        if (MachineOf(nodeId) is not { } machineId) { await directory.Put(nodeId, cid, bytes); return; }
        LinkReplies.Payload(await Send(machineId, NodeOperations.Put, new ShardPut(cid, bytes), cancellationToken));
    }

    public async Task<byte[]?> Get(string nodeId, string cid, CancellationToken cancellationToken)
    {
        if (MachineOf(nodeId) is not { } machineId) { return await directory.Get(nodeId, cid); }
        return await Read(machineId, NodeOperations.Get, new ShardGet(cid), cancellationToken);
    }

    public async Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce, CancellationToken cancellationToken)
    {
        if (MachineOf(nodeId) is not { } machineId) { return await directory.Prove(nodeId, cid, nonce); }
        return await Read(machineId, NodeOperations.Prove, new ShardProve(cid, nonce), cancellationToken);
    }

    public async Task Drop(string nodeId, string cid, CancellationToken cancellationToken)
    {
        if (MachineOf(nodeId) is not { } machineId) { await directory.Discard(nodeId, cid); return; }
        LinkReplies.Payload(await Send(machineId, NodeOperations.Drop, new ShardDrop(cid), cancellationToken));
    }

    private static string? MachineOf(string nodeId) => nodeId.StartsWith(Prefix, StringComparison.Ordinal) ? nodeId[Prefix.Length..] : null;

    // A node that cannot be reached, or answers with a refusal, holds nothing this read can use.
    private async Task<byte[]?> Read<T>(string machineId, string operation, T request, CancellationToken cancellationToken)
    {
        try { return serializer.Deserialize<byte[]?>(LinkReplies.Payload(await Send(machineId, operation, request, cancellationToken))); }
        catch (Exception unreachable) when (unreachable is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    // R119: anything short of the node's own answer (no transport, no link, a link that failed mid-call) is unavailability.
    private async Task<LinkReply> Send<T>(string machineId, string operation, T request, CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity($"NodeShard {operation}", ActivityKind.Client);
        activity?.SetTag("digitalbrain.node-machine", machineId);
        if (transport is null) { throw new ServiceUnavailableException($"Node {Prefix}{machineId} is a volunteer and this host has no machine links."); }
        try
        {
            var link = await transport.Connect(machineId, cancellationToken).ConfigureAwait(false);
            var parent = Activity.Current;
            return await link.Send(new LinkRequest(operation, serializer.SerializeToArray(request), parent?.Id, parent?.TraceStateString, Guid.NewGuid()), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failed.Message);
            throw new ServiceUnavailableException($"Node {Prefix}{machineId} could not be reached: {failed.GetType().Name}: {failed.Message}");
        }
    }
}
