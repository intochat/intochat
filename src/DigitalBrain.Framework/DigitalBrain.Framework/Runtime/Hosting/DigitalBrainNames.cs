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
    public const string BrainStorageConfigurationKey = "DigitalBrain:Platform:Storage";
    public const string BrainStorageDirectoryConfigurationKey = BrainStorageConfigurationKey + ":Directory";
    public const string BrainStorageWriteFormatConfigurationKey = BrainStorageConfigurationKey + ":WriteFormat";
    public const string ConfiguredCustodyAcknowledgedConfigurationKey = BrainStorageConfigurationKey + ":ConfiguredCustodyAcknowledged";
    // Shared with the Windows module without a dependency on the Platform implementation.
    public const string CustodyConfigurationKey = BrainStorageConfigurationKey + ":Custody";
}
