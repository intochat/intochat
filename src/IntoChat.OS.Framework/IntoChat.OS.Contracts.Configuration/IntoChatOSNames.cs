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
    // HostedRuntimeOptions' section; a runtime deployed beside its own Orleans grain state must never carry it.
    public const string HostingConfigurationKey = "IntoChatOS:Hosting";
    public const string IntegrationsSectionName = "IntoChatOS:Integrations";
    // Where the deployment tells the runtime its vault lives.
    public const string KeyVaultUriConfigurationKey = "IntoChatOS:KeyVault:Uri";
}
