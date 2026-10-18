using Pulumi;

namespace DigitalBrain.Deployment.Runtime;

public enum RuntimeStorageMode { Durable, Memory }

public enum RuntimeOwnership { BlobLease, FileLock }

public sealed record RuntimeOptions
{
    public required string Image { get; init; }
    // Orleans__ClusterId and Orleans__ServiceId, for example intochat-test.
    public required string ClusterId { get; init; }
    public required string ServiceId { get; init; }
    // The brain's master key; it lands in Key Vault as master-key and reaches the silo as a vault reference.
    public required Input<string> MasterKey { get; init; }
    public string? AppName { get; init; }
    public double Cpu { get; init; } = 0.5;
    public string Memory { get; init; } = "1Gi";
    // One silo until brain-affinity placement exists (W5 ADR A1b).
    public int MinReplicas { get; init; } = 1;
    public int MaxReplicas { get; init; } = 1;
    // Suffix of every versioned store: grainstate-v1, brainlog-v1, brainlease-v1, /data/brain/v1.
    public string StorageFormat { get; init; } = "v1";
    // Memory runs the silo without durable stores (bootstrap only); Durable needs the Files share and the lease.
    public RuntimeStorageMode StorageMode { get; init; } = RuntimeStorageMode.Durable;
    public RuntimeOwnership Ownership { get; init; } = RuntimeOwnership.BlobLease;
    public int BrainLogQuotaGb { get; init; } = 20;
    // The image's non-root user (the .NET images' "app" user), which owns the mounted brain log.
    public int AppUid { get; init; } = 1654;
    public int AppGid { get; init; } = 1654;
    public int TerminationGracePeriodSeconds { get; init; } = 60;
    public string HealthPath { get; init; } = "/health";
    // Further plain settings and secrets; secrets go to Key Vault. Neither may override what DeployRuntime sets.
    public IReadOnlyDictionary<string, Input<string>> Settings { get; init; } = new Dictionary<string, Input<string>>();
    public IReadOnlyDictionary<string, Input<string>> Secrets { get; init; } = new Dictionary<string, Input<string>>();
}
