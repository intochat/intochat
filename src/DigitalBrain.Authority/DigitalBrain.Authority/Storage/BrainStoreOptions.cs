namespace DigitalBrain.Authority.Storage;

public sealed class BrainStoreOptions
{
    public const string Key = DigitalBrain.Framework.DigitalBrainNames.BrainStorageConfigurationKey;
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigitalBrain", "runtime");
    public DigitalBrain.Framework.Parts.MasterKeyCustodyKind Custody { get; set; }
    public bool ConfiguredCustodyAcknowledged { get; set; }
    // Shipped OS config writes Legacy until the release after v0.1.0; flip by setting WriteFormat=Framed.
    public BrainLogFormat WriteFormat { get; set; } = BrainLogFormat.Legacy;
    public BrainLogOwnershipOptions Ownership { get; set; } = new();
}

public enum BrainLogOwnershipKind { WriterLock, BlobLease }

// WriterLock (the default) keeps the local writer.lock; BlobLease proves single-writer ownership across replicas sharing a file share.
public sealed class BrainLogOwnershipOptions
{
    public BrainLogOwnershipKind Kind { get; set; }
    // The lease container, reached with the host's TokenCredential (managed identity in Azure).
    public Uri? ContainerUri { get; set; }
    // Alternative to ContainerUri for development storage; ContainerName names the container inside it.
    public string? ConnectionString { get; set; }
    public string ContainerName { get; set; } = "brainlease-v1";
    // How long a claim waits for the previous owner's lease to be released or expire.
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromSeconds(90);
}
