using System.Text.Json.Serialization;

namespace DigitalBrain.Brains;

/// <summary>
/// Where a brain's state and journal live and how long it persists.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Ephemeral), "ephemeral")]
[JsonDerivedType(typeof(Persistent), "persistent")]
public abstract record BrainResidency
{
    private BrainResidency() { }

    /// <summary>True when the brain lives purely in RAM and has not yet been claimed into durable storage.</summary>
    public bool IsEphemeral => this is Ephemeral;

    /// <summary>
    /// Pure in-memory brain residency for Day-0 unauthenticated visitors.
    /// Auto-evicted after <see cref="IdleTtl"/> of inactivity or under memory pressure; promoted in-place via <see cref="IBrain.Claim"/>.
    /// </summary>
    public sealed record Ephemeral : BrainResidency
    {
        public const int DefaultMaxMemoryBytes = 16 * 1024 * 1024;
        public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromHours(24);
        public const int DefaultMaxTrialTurns = 15;

        public static Ephemeral Default { get; } = new();

        public int MaxMemoryBytes { get; init; }
        public TimeSpan IdleTtl { get; init; }
        public int MaxTrialTurns { get; init; }

        [JsonConstructor]
        public Ephemeral(int maxMemoryBytes, TimeSpan idleTtl, int maxTrialTurns)
        {
            if (maxMemoryBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(maxMemoryBytes), "MaxMemoryBytes must be positive."); }
            if (idleTtl < TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(idleTtl), "IdleTtl cannot be negative."); }
            if (maxTrialTurns < 0) { throw new ArgumentOutOfRangeException(nameof(maxTrialTurns), "MaxTrialTurns cannot be negative."); }
            MaxMemoryBytes = maxMemoryBytes;
            IdleTtl = idleTtl;
            MaxTrialTurns = maxTrialTurns;
        }

        public Ephemeral(int maxMemoryBytes = DefaultMaxMemoryBytes, TimeSpan? idleTtl = null, int maxTrialTurns = DefaultMaxTrialTurns)
            : this(maxMemoryBytes, idleTtl ?? DefaultIdleTtl, maxTrialTurns)
        {
        }
    }

    /// <summary>
    /// Durable brain residency backed by the encrypted brain journal and durable storage, owned by <see cref="OwnerPrincipalId"/>.
    /// </summary>
    public sealed record Persistent : BrainResidency
    {
        public string OwnerPrincipalId { get; init; }

        [JsonConstructor]
        public Persistent(string ownerPrincipalId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
            OwnerPrincipalId = ownerPrincipalId;
        }
    }
}

/// <summary>
/// Runtime residency telemetry surfaced on session/status endpoints for UI binding.
/// </summary>
public sealed record BrainResidencySnapshot(
    bool IsEphemeral,
    long? TtlSeconds,
    int? RemainingTurns,
    long MemoryUsedBytes,
    long? MemoryLimitBytes,
    string? OwnerPrincipalId = null)
{
    public static BrainResidencySnapshot Persistent(string? ownerPrincipalId = null, long memoryUsedBytes = 0)
        => new(
            IsEphemeral: false,
            TtlSeconds: null,
            RemainingTurns: null,
            MemoryUsedBytes: memoryUsedBytes,
            MemoryLimitBytes: null,
            OwnerPrincipalId: ownerPrincipalId);
}

/// <summary>
/// Thrown when an ephemeral brain's in-memory journal exceeds its configured <see cref="BrainResidency.Ephemeral.MaxMemoryBytes"/>.
/// </summary>
public sealed class BrainMemoryLimitExceededException : InvalidOperationException
{
    public string BrainId { get; }
    public long RequestedBytes { get; }
    public long MaxMemoryBytes { get; }

    public BrainMemoryLimitExceededException(string brainId, long requestedBytes, long maxMemoryBytes)
        : base($"Ephemeral brain '{brainId}' exceeded its memory limit ({requestedBytes} bytes > {maxMemoryBytes} bytes). Sign in to claim this brain into persistent storage.")
    {
        BrainId = brainId;
        RequestedBytes = requestedBytes;
        MaxMemoryBytes = maxMemoryBytes;
    }
}
