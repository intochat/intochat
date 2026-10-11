namespace DigitalBrain.Platform.Mesh;

// T23-2: what one machine asks a volunteer node over their link. The putter is the link's authenticated machine, never
// a field, and no shard index, segment id or manifest ref crosses (Q23): a node sees ciphertext and content ids only.
internal static class NodeOperations
{
    public const string Put = "node.shard.put";
    public const string Get = "node.shard.get";
    public const string Prove = "node.shard.prove";
    public const string Drop = "node.shard.drop";

    public static readonly string[] All = [Put, Get, Prove, Drop];

    public static string NodeIdOf(string machineId) => StorageNodes.IdOf(machineId);
}

[GenerateSerializer, Alias("platform.mesh.node.put")]
internal sealed record ShardPut([property: Id(0)] string Cid, [property: Id(1)] byte[] Bytes);

[GenerateSerializer, Alias("platform.mesh.node.get")]
internal sealed record ShardGet([property: Id(0)] string Cid);

[GenerateSerializer, Alias("platform.mesh.node.prove")]
internal sealed record ShardProve([property: Id(0)] string Cid, [property: Id(1)] byte[] Nonce);

[GenerateSerializer, Alias("platform.mesh.node.drop")]
internal sealed record ShardDrop([property: Id(0)] string Cid);
