namespace IntoChat.OS.Contracts.Configuration;

public static class IntoChatOSNames
{
    public const string GrainStateContainer = "os-state";
    public const string GrainStateContainerKey = "IntoChatOS:Orleans:GrainStateContainer";

    public const string Storage = "storage";
    public const string Clustering = "clustering";
    public const string Reminders = "reminders";
    public const string GrainState = "grainstate";
    public const string ClusterGrainStorage = "cluster";
    // Nothing registers this store. The name remains so old configuration that names it is still a known key.
    public const string LegacyClusterGrainStorage = "cluster.legacy";
    public const string PlatformGrainStorage = "platform";
    // Pinned equal to DigitalBrain.Runtime.Orleans.IntoChatOSNames (and Topology.ToConfigValue()) by TopologyKeysMatchRuntimeFacts.
    // Kept because their consumers (ServiceDefaults, AppHost, Aspire hosting, module installers) stay Orleans-free (03-13).
    // Where local state lives (<Home>/state, <Home>/secrets); unset means ~/.intochat.
    public const string HomeKey = "IntoChatOS:Home";
    public const string TopologyKey = "IntoChatOS:Topology";
    public const string TopologyLocal = "local";
    public const string TopologyLocalCloud = "local+cloud";
    // The earlier spelling of local+cloud: still accepted (the runtime warns), never written.
    public const string TopologyLocalCloudAlias = "local-cloud";
    public const string TopologyCloud = "cloud";
    // Widens binding beyond loopback on local and local+cloud: a comma list of IP literals or localhost (0.0.0.0 or :: for every
    // interface). Unset means loopback only.
    public const string BindKey = "IntoChatOS:Bind";
    // Topology=cloud: the one storage account (tables, blobs, queues) and the Key Vault the cloud topology reads
    // (the same strings as DigitalBrain.Runtime.Orleans IntoChatOSNames.CloudStorageKey and CloudKeyVaultKey, P2-D-22).
    public const string CloudStorageConfigurationKey = "IntoChatOS:Cloud:Storage";
    public const string CloudKeyVaultConfigurationKey = "IntoChatOS:Cloud:KeyVault";
    public const string MasterKeyConfigurationKey = "IntoChatOS:MasterKey";
    public const string MasterKeyEnvironmentVariable = "IntoChatOS__MasterKey";
    public const string BrainStorageConfigurationKey = "IntoChatOS:Platform:Storage";
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
    public const string IntegrationsSectionName = "IntoChatOS:Integrations";

    // Single source of truth for modules switched off by default in local / local+cloud runs (shared by Kernel and AppHost).
    public static readonly IReadOnlyList<string> LocalDisabledModules =
        ["postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "github", "azure", "roslyn", "salesforce"];
}
