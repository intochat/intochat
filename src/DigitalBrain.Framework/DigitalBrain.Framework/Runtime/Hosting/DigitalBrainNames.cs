namespace DigitalBrain.Framework;

public static class DigitalBrainNames
{
    public const string GrainStateContainer = "digitalbrain-v2-state";
    public const string GrainStateContainerKey = "DigitalBrain:Orleans:GrainStateContainer";

    public const string Storage = "storage";
    public const string Clustering = "clustering";
    public const string Reminders = "reminders";
    public const string GrainState = "grainstate";
    public const string ClusterGrainStorage = "cluster";
    public const string PlatformGrainStorage = "platform";
    public const string MasterKeyConfigurationKey = "DigitalBrain:MasterKey";
    public const string MasterKeyEnvironmentVariable = "DigitalBrain__MasterKey";
    // PartsOptions.Custody's key, here so a per-OS module reads it without the Platform ring.
    public const string CustodyConfigurationKey = "DigitalBrain:Platform:Parts:Custody";
}
