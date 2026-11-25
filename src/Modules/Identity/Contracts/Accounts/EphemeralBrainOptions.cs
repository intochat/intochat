using DigitalBrain.Brains;

namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

/// <summary>
/// Configuration for Day-0 ephemeral in-memory brains (Slice OSS-2).
/// </summary>
public sealed class EphemeralBrainOptions
{
    public const string SectionName = "IntoChatOS:Ephemeral";

    /// <summary>Maximum in-memory journal size per ephemeral brain (default 16 MiB).</summary>
    public int MaxMemoryBytes { get; set; } = BrainResidency.Ephemeral.DefaultMaxMemoryBytes;

    /// <summary>Idle time-to-live before an unclaimed ephemeral brain is evicted from RAM (default 24h).</summary>
    public TimeSpan IdleTtl { get; set; } = BrainResidency.Ephemeral.DefaultIdleTtl;

    /// <summary>Maximum fast-model assistant turns before prompting the visitor to claim their workspace (default 15).</summary>
    public int MaxTrialTurns { get; set; } = BrainResidency.Ephemeral.DefaultMaxTrialTurns;

    /// <summary>Maximum concurrent ephemeral brains held in RAM on one host before LRU eviction.</summary>
    public int MaxActiveBrains { get; set; } = 512;

    /// <summary>Aggregate RAM budget across all active ephemeral brains before memory-pressure LRU eviction (default 512 MiB).</summary>
    public long TotalMemoryBudgetBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Maximum ephemeral brain mints per minute per IP prefix (/24 IPv4 or /64 IPv6).</summary>
    public int MintPerMinutePerIpPrefix { get; set; } = 20;

    /// <summary>Background eviction sweep interval.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public BrainResidency.Ephemeral ToResidency() => new(MaxMemoryBytes, IdleTtl, MaxTrialTurns);
}
