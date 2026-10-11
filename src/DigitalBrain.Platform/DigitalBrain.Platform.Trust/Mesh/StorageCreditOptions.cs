namespace DigitalBrain.Platform.Mesh;

// T23-11: what a passed audit earns the node's owner, and how many passes one putter account can earn a node a day.
public sealed class StorageCreditOptions
{
    public const string Key = "DigitalBrain:Platform:StorageCredits";

    public long MicrosPerGiBPass { get; set; } = 1_000;

    public int MaxPassesPerPutterPerNodePerDay { get; set; } = 64;

    public void Validate()
    {
        if (MicrosPerGiBPass < 0) { throw new InvalidOperationException("Storage credits need a non-negative MicrosPerGiBPass."); }
        if (MaxPassesPerPutterPerNodePerDay < 0) { throw new InvalidOperationException("Storage credits need a non-negative MaxPassesPerPutterPerNodePerDay."); }
    }
}
