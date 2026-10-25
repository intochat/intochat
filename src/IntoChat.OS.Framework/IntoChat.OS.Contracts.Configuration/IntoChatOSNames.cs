namespace IntoChat.OS.Framework;

public static class IntoChatOSNames
{
    public const string GrainStateContainer = "os-state";
    public const string GrainStateContainerKey = "IntoChatOS:Orleans:GrainStateContainer";

    public const string Storage = "storage";
    public const string Clustering = "clustering";
    public const string Reminders = "reminders";
    public const string GrainState = "grainstate";
    public const string ClusterGrainStorage = "cluster";
    public const string PlatformGrainStorage = "platform";
    // Where local state lives (<Home>/state, <Home>/secrets); unset means ~/.intochat.
    // Phase 3 re-points this at DigitalBrain.Runtime.Orleans TopologySettings.
    public const string HomeKey = "IntoChatOS:Home";
    // Phase 3 re-points this at DigitalBrain.Runtime.Orleans TopologySettings.
    public const string TopologyKey = "IntoChatOS:Topology";
    // Phase 3 re-points this at DigitalBrain.Runtime.Orleans TopologySettings.
    public const string TopologyLocal = "local";
    // Phase 3 re-points this at DigitalBrain.Runtime.Orleans TopologySettings.
    public const string TopologyLocalCloud = "local-cloud";
    // Phase 3 re-points this at DigitalBrain.Runtime.Orleans TopologySettings.
    public const string TopologyCloud = "cloud";
    public const string MasterKeyConfigurationKey = "IntoChatOS:MasterKey";
    public const string MasterKeyEnvironmentVariable = "IntoChatOS__MasterKey";
    public const string BrainStorageConfigurationKey = "IntoChatOS:Platform:Storage";
    public const string BrainStorageDirectoryConfigurationKey = BrainStorageConfigurationKey + ":Directory";
    public const string BrainStorageWriteFormatConfigurationKey = BrainStorageConfigurationKey + ":WriteFormat";
    public const string ConfiguredCustodyAcknowledgedConfigurationKey = BrainStorageConfigurationKey + ":ConfiguredCustodyAcknowledged";
    // Shared with the Windows module without a dependency on the Platform implementation.
    public const string CustodyConfigurationKey = BrainStorageConfigurationKey + ":Custody";
    // BrainStoreOptions.Ownership: which mechanism proves one writer per brain, and the Blob lease container it uses.
    public const string BrainStorageOwnershipKindConfigurationKey = BrainStorageConfigurationKey + ":Ownership:Kind";
    public const string BrainStorageOwnershipContainerUriConfigurationKey = BrainStorageConfigurationKey + ":Ownership:ContainerUri";
    // BrainLogOwnershipKind's member names, as configuration spells them.
    public const string BrainStorageOwnershipBlobLease = "BlobLease";
    public const string BrainStorageOwnershipWriterLock = "WriterLock";
    // Run-mode marker the AppHost projects to the processes it starts (value "run"). The literal is pinned by RunModeMarkerFacts;
    // the loopback guard for the Open posture reads it, so it must not be renamed or derived.
    public const string HostingModeConfigurationKey = "IntoChatOS:Hosting:Mode";
    public const string HostingModeRun = "run";
    public const string IntegrationsSectionName = "IntoChatOS:Integrations";
    // Where the deployment tells the runtime its vault lives.
    public const string KeyVaultUriConfigurationKey = "IntoChatOS:KeyVault:Uri";
}
