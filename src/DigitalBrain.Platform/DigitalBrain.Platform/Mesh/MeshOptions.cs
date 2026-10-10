namespace DigitalBrain.Platform.Mesh;

// Wide coding because home nodes sleep: any DataShards of DataShards+ParityShards rebuild a segment (Q20).
public sealed class MeshOptions
{
    public const string Key = "DigitalBrain:Platform:Mesh";

    public int DataShards { get; set; } = 8;

    public int ParityShards { get; set; } = 16;

    public int ChallengesPerShard { get; set; } = 16;

    // Whole copies of the sealed manifest; it is small, and a ref names every node that holds one.
    public int ManifestReplicas { get; set; } = 3;

    // Platform-owned nodes registered at startup so the mesh exists from day 0 (Q25).
    public int SeedNodes { get; set; }

    public void Validate()
    {
        if (DataShards < 1 || ParityShards < 1 || DataShards + ParityShards > 255)
        { throw new InvalidOperationException("Mesh needs DataShards >= 1, ParityShards >= 1 and at most 255 shards in total."); }
        if (ChallengesPerShard < 1) { throw new InvalidOperationException("Mesh needs at least one storage challenge per shard."); }
        if (ManifestReplicas < 1) { throw new InvalidOperationException("Mesh needs at least one manifest replica."); }
        if (SeedNodes < 0) { throw new InvalidOperationException("Mesh needs a non-negative SeedNodes count."); }
    }
}
