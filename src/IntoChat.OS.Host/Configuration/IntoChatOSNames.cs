namespace IntoChat.OS.Host.Configuration;

public static class IntoChatOSNames
{
    public const string DefaultOwnerBrainId = "owner";
    public const string GrainStateContainer = "os-state";
    public const string GrainStateContainerKey = "IntoChatOS:Orleans:GrainStateContainer";

    public const string Storage = "storage";
    public const string Clustering = "clustering";
    public const string Reminders = "reminders";
    public const string GrainState = "grainstate";
    public const string ClusterGrainStorage = "cluster";
    public const string PlatformGrainStorage = "platform";
    // Pinned equal to IntoChat.OS.Kernel.Composition.Kernel.OsRuntimeOptions.Value (and Topology.ToConfigValue()) by TopologyKeysMatchRuntimeFacts.
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
    // (the same strings as IntoChat.OS.Kernel.Composition.Kernel.OsRuntimeOptions.Value.CloudStorageKey and CloudKeyVaultKey, P2-D-22).
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
    public const string ShellEnabledKey = "IntoChatOS:Shell:Enabled";
    public const string FrontEnabledKey = "IntoChatOS:Http:Front";
    public const string LocalModelKey = "IntoChatOS:Local:Model";
    public const string LocalDefaultChatModel = "qwen3.5:4b";
    public const string LocalOllamaEndpoint = "http://localhost:11434";

    // The modules switched off by default in local and local+cloud runs: the one list every host and the kernel read.
    public static readonly IReadOnlyList<string> LocalDisabledModules =
        ["postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "azure", "roslyn"];

    // The canonical spelling of a topology value: unset is local, the earlier local-cloud spelling is local+cloud, and an
    // unknown value is null (the caller decides whether that stops the run).
    public static string? CanonicalTopology(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return TopologyLocal; }
        var trimmed = value.Trim();
        if (string.Equals(trimmed, TopologyLocalCloudAlias, StringComparison.OrdinalIgnoreCase)) { return TopologyLocalCloud; }
        foreach (var known in (string[])[TopologyLocal, TopologyLocalCloud, TopologyCloud])
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase)) { return known; }
        }
        return null;
    }

    // Local and local+cloud run without containers, so the LocalDisabledModules default applies to them.
    public static bool UsesLocalModuleDefaults(string? topology) => CanonicalTopology(topology) is TopologyLocal or TopologyLocalCloud;
}
