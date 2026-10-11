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

    // T23-3: a quarter of the registered link's 64 MiB frame cap; a segment is at most DataShards of these.
    public int MaxShardBytes { get; set; } = 16 * 1024 * 1024;

    // T23-5: a volunteer whose last heartbeat is older than this is offline; seeds are online while the trust host runs.
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromMinutes(3);

    // T23-9: how often a part's holder audits the segments its head reaches (read by both sides, R118).
    public TimeSpan AuditEvery { get; set; } = TimeSpan.FromHours(6);

    public void Validate()
    {
        if (DataShards < 1 || ParityShards < 1 || DataShards + ParityShards > 255)
        { throw new InvalidOperationException("Mesh needs DataShards >= 1, ParityShards >= 1 and at most 255 shards in total."); }
        if (ChallengesPerShard < 1) { throw new InvalidOperationException("Mesh needs at least one storage challenge per shard."); }
        if (ManifestReplicas < 1) { throw new InvalidOperationException("Mesh needs at least one manifest replica."); }
        if (SeedNodes < 0) { throw new InvalidOperationException("Mesh needs a non-negative SeedNodes count."); }
        if (MaxShardBytes < 16) { throw new InvalidOperationException("Mesh needs MaxShardBytes of at least 16."); }
        if (OfflineAfter <= TimeSpan.Zero) { throw new InvalidOperationException("Mesh needs a positive OfflineAfter."); }
        if (AuditEvery <= TimeSpan.Zero) { throw new InvalidOperationException("Mesh needs a positive AuditEvery."); }
    }
}
