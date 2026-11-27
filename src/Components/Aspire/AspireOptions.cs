using DigitalBrain.Modules.Options;

namespace IntoChat.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    // How long one instance may take to come back ready during a roll before the roll goes back to the previous build.
    public TimeSpan SiloStepBudget { get; set; } = TimeSpan.FromMinutes(3);

    // Retained for configuration compatibility. Membership is now delivered by Orleans'
    // change stream; no deployment timer uses this former polling interval.
    public TimeSpan ClusterSampling { get; set; } = TimeSpan.FromSeconds(5);

    // Selects whether candidate verification runs in leased devcontainer slots (default, with automatic Process fallback when Docker is unavailable) or local headless OS processes.
    public CandidateHostMode CandidateHostMode { get; set; } = CandidateHostMode.AgentDevSlots;

    // Maximum concurrent leased devcontainer slots in Plane 2 (agent-dev-slots).
    public int MaxCandidateSlots { get; set; } = Candidates.CandidateSlotPoolOptions.DefaultMaxSlots;

    // First host TCP port used for dynamic lock-free slot port range allocation (avoids live 5080/5081 and legacy 5190).
    public int CandidateSlotBasePort { get; set; } = Candidates.CandidateSlotPoolOptions.DefaultBasePort;

    // Number of contiguous ports reserved per leased slot (HTTP, MCP, Azurite blob/table, Postgres, ClickHouse + headroom).
    public int CandidateSlotPortStride { get; set; } = Candidates.CandidateSlotPoolOptions.DefaultPortStride;

    // Maximum duration a slot lease stays active before deterministic reclamation.
    public TimeSpan CandidateSlotLeaseTimeout { get; set; } = Candidates.CandidateSlotPoolOptions.DefaultLeaseTimeoutValue;

    // Enables warm dependency sidecar reuse (Azurite, Postgres, ClickHouse) across leases while isolating per-lease DBs and $HOME.
    public bool EnableWarmSlotReuse { get; set; } = true;

    // Unprivileged Linux UID for candidate container execution (must be > 0, never root).
    public int CandidateUserId { get; set; } = Candidates.CandidateSlotPoolOptions.DefaultCandidateUserId;

    // Unprivileged Linux GID for candidate container execution (must be > 0, never root).
    public int CandidateGroupId { get; set; } = Candidates.CandidateSlotPoolOptions.DefaultCandidateGroupId;

    // How long a command asked by a caller who is not a person waits for the person's answer before it expires.
    public TimeSpan CommandConfirmationExpiry { get; set; } = TimeSpan.FromMinutes(5);

    // Commands that a non-person may never request (refused as "person-only" with no confirmation card created).
    public string[] PersonOnlyCommands { get; set; } = ["reset-state"];

    // Bounded wait for a command executed on the AppHost bridge.
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal Candidates.CandidateSlotPoolOptions ToSlotPoolOptions() => new()
    {
        MaxSlots = MaxCandidateSlots,
        BasePort = CandidateSlotBasePort,
        PortStride = CandidateSlotPortStride,
        DefaultLeaseTimeout = CandidateSlotLeaseTimeout,
        EnableWarmSlotReuse = EnableWarmSlotReuse,
        CandidateUserId = CandidateUserId,
        CandidateGroupId = CandidateGroupId,
    };

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SiloStepBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClusterSampling, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CommandConfirmationExpiry, TimeSpan.Zero);
        // The framework caps a plan that needs a person at seven days; a longer value would be capped silently, so it is refused.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CommandConfirmationExpiry, TimeSpan.FromDays(7));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CommandTimeout, TimeSpan.Zero);
        if (!Enum.IsDefined(CandidateHostMode))
        {
            throw new ArgumentOutOfRangeException(nameof(CandidateHostMode), CandidateHostMode, "Unknown CandidateHostMode.");
        }

        ToSlotPoolOptions().Validate();
    }
}
