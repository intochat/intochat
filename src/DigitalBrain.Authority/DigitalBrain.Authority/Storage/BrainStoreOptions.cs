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

// Bound by name from Ownership:Kind; deployment writes the name explicitly for either mode.
public enum BrainLogOwnershipKind { WriterLock = 0, BlobLease = 1 }

// WriterLock (the default) keeps the local writer.lock; BlobLease proves single-writer ownership across replicas sharing a file share.
public sealed class BrainLogOwnershipOptions
{
    public BrainLogOwnershipKind Kind { get; set; } = BrainLogOwnershipKind.WriterLock;
    // The lease container, reached with the host's TokenCredential (managed identity in Azure).
    public Uri? ContainerUri { get; set; }
    // Alternative to ContainerUri for development storage; ContainerName names the container inside it.
    public string? ConnectionString { get; set; }
    public string ContainerName { get; set; } = "brainlease-v1";
    // A user-assigned managed identity's client id; AZURE_CLIENT_ID is the fallback.
    public string? ManagedIdentityClientId { get; set; }
    // How long one claim waits for a held lease. It stays inside the Orleans call timeout; a longer handover is the caller's retry.
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromSeconds(25);
    // The longest one journal or checkpoint write may take before the lease counts as lost; well under the 20 s fence margin.
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
