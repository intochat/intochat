namespace DigitalBrain.Platform.Mesh;

// T23-1/T23-4: this machine as a volunteer storage node. Offering nothing (the default) means it is not one.
public sealed class StorageNodeOptions
{
    public const string Key = "DigitalBrain:Platform:StorageNode";

    public long OfferedBytes { get; set; }

    // 0 = a quarter of the offer, so one account cannot fill the node.
    public long MaxBytesPerAccount { get; set; }

    // null = {Parts:MachineDirectory}/node.
    public string? Directory { get; set; }

    public TimeSpan HeartbeatEvery { get; set; } = TimeSpan.FromSeconds(60);

    public long ShareOfAccount => MaxBytesPerAccount > 0 ? MaxBytesPerAccount : OfferedBytes / 4;

    public void Validate()
    {
        if (OfferedBytes < 0) { throw new InvalidOperationException($"{Key}:{nameof(OfferedBytes)} cannot be negative."); }
        if (MaxBytesPerAccount < 0) { throw new InvalidOperationException($"{Key}:{nameof(MaxBytesPerAccount)} cannot be negative."); }
        if (HeartbeatEvery <= TimeSpan.Zero) { throw new InvalidOperationException($"{Key}:{nameof(HeartbeatEvery)} must be positive."); }
    }
}
