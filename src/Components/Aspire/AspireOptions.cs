using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Execution;

namespace IntoChat.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    // How long one instance may take to come back ready during a roll before the roll goes back to the previous build.
    public TimeSpan SiloStepBudget { get; set; } = TimeSpan.FromMinutes(3);

    // Retained for configuration compatibility. Membership is now delivered by Orleans'
    // change stream; no deployment timer uses this former polling interval.
    public TimeSpan ClusterSampling { get; set; } = TimeSpan.FromSeconds(5);

    // Selects whether candidate verification runs in Docker containers, local headless OS processes, leased devcontainer slots, or ACA Dynamic Sessions Kata MicroVMs.
    public CandidateHostMode CandidateHostMode { get; set; } = CandidateHostMode.Docker;

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

    // Azure Container Apps Dynamic Sessions pool management endpoint for headless Plane 2 cloud fallback (dormant when unset).
    public string? AcaPoolManagementEndpoint { get; set; }

    // Azure Dynamic Sessions management plane API version for Plane 2 candidate sessions.
    public string AcaApiVersion { get; set; } = AcaDynamicSessionsOptions.DefaultApiVersion;

    // Entra ID / Managed Identity token scope for Plane 2 ACA Dynamic Sessions.
    public string AcaTokenScope { get; set; } = AcaDynamicSessionsOptions.DefaultTokenScope;

    // Optional user-assigned Managed Identity client ID for Plane 2 ACA Dynamic Sessions.
    public string? AcaManagedIdentityClientId { get; set; }

    // Strict per-execution timeout for Plane 2 ACA Dynamic Sessions calls (build gate, publish, run).
    public TimeSpan AcaExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // Strict readiness timeout when waiting for a Plane 2 ACA Dynamic Sessions candidate microVM to become healthy.
    public TimeSpan AcaSessionHealthTimeout { get; set; } = TimeSpan.FromMinutes(3);

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

    internal AcaDynamicSessionsOptions ToAcaDynamicSessionsOptions() => new()
    {
        PoolManagementEndpoint = AcaPoolManagementEndpoint,
        ApiVersion = AcaApiVersion,
        TokenScope = AcaTokenScope,
        ManagedIdentityClientId = AcaManagedIdentityClientId,
        ExecutionTimeout = AcaExecutionTimeout,
        SessionHealthTimeout = AcaSessionHealthTimeout,
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
        ToAcaDynamicSessionsOptions().Validate();
    }
}
