using System.Text.RegularExpressions;
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

    // Cloud ACA Consumption Job (Microsoft.App/jobs) candidate host configuration (used when CandidateHostMode == AcaJob).
    public AcaJobCandidateOptions AcaJob { get; set; } = new();

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
        if (CandidateHostMode == CandidateHostMode.AcaJob)
        {
            AcaJob.Validate();
        }
    }
}

public sealed partial class AcaJobCandidateOptions
{
    public const int MaxReplicaTimeoutSeconds = 900;
    public const int MaxParallelism = 2;
    public const double MaxCpu = 2.0;
    public const string DefaultArmEndpoint = "https://management.azure.com";
    public const string DefaultApiVersion = "2025-01-01";
    public const string DefaultContainerName = "devslot";

    public static readonly string[] AllowedArmHosts =
    [
        "management.azure.com",
        "management.usgovcloudapi.net",
        "management.chinacloudapi.cn",
        "management.microsoftazure.de",
    ];

    public static readonly string[] AllowedMemoryTiers =
    [
        "0.5Gi", "1Gi", "1.5Gi", "2Gi", "2.5Gi", "3Gi", "3.5Gi", "4Gi",
    ];

    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string JobName { get; set; } = "";
    public string ArmEndpoint { get; set; } = DefaultArmEndpoint;
    public string ApiVersion { get; set; } = DefaultApiVersion;
    public string ContainerName { get; set; } = DefaultContainerName;
    public string? ImageOverride { get; set; }
    public int ReplicaTimeoutSeconds { get; set; } = MaxReplicaTimeoutSeconds;
    public int Parallelism { get; set; } = 1;
    public double Cpu { get; set; } = MaxCpu;
    public string Memory { get; set; } = "4Gi";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public Uri ValidateAndGetArmBaseUri()
    {
        if (!Guid.TryParse(SubscriptionId, out var parsedSubscriptionId))
        {
            throw new ArgumentException("AcaJob.SubscriptionId must be a valid GUID.", nameof(SubscriptionId));
        }

        SubscriptionId = parsedSubscriptionId.ToString("D");

        if (string.IsNullOrWhiteSpace(ResourceGroup)
            || ResourceGroup.Contains("..", StringComparison.Ordinal)
            || !ResourceGroupPattern().IsMatch(ResourceGroup))
        {
            throw new ArgumentException("AcaJob.ResourceGroup is missing or contains invalid characters.", nameof(ResourceGroup));
        }

        if (string.IsNullOrWhiteSpace(JobName) || !JobNamePattern().IsMatch(JobName))
        {
            throw new ArgumentException("AcaJob.JobName must be a valid lowercase Azure Container Apps Job name (2-32 chars, alphanumeric or '-').", nameof(JobName));
        }

        if (string.IsNullOrWhiteSpace(ContainerName) || !ContainerNamePattern().IsMatch(ContainerName))
        {
            throw new ArgumentException("AcaJob.ContainerName must be a valid lowercase container name.", nameof(ContainerName));
        }

        if (string.IsNullOrWhiteSpace(ApiVersion) || !ApiVersionPattern().IsMatch(ApiVersion))
        {
            throw new ArgumentException("AcaJob.ApiVersion must match YYYY-MM-DD or YYYY-MM-DD-preview.", nameof(ApiVersion));
        }

        if (!Uri.TryCreate(ArmEndpoint, UriKind.Absolute, out var armUri)
            || !string.Equals(armUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !armUri.IsDefaultPort
            || !string.IsNullOrEmpty(armUri.UserInfo)
            || !string.IsNullOrEmpty(armUri.Query)
            || !string.IsNullOrEmpty(armUri.Fragment)
            || (armUri.AbsolutePath is not "" and not "/")
            || !AllowedArmHosts.Contains(armUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"AcaJob.ArmEndpoint must be an HTTPS Azure Resource Manager host ({string.Join(", ", AllowedArmHosts)}).", nameof(ArmEndpoint));
        }

        if (ReplicaTimeoutSeconds < 1 || ReplicaTimeoutSeconds > MaxReplicaTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(ReplicaTimeoutSeconds), ReplicaTimeoutSeconds, $"Policy Rule B requires 1 <= ReplicaTimeoutSeconds <= {MaxReplicaTimeoutSeconds}.");
        }

        if (Parallelism < 1 || Parallelism > MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, $"Policy Rule B requires 1 <= Parallelism <= {MaxParallelism}.");
        }

        if (double.IsNaN(Cpu) || double.IsInfinity(Cpu) || Cpu <= 0 || Cpu > MaxCpu)
        {
            throw new ArgumentOutOfRangeException(nameof(Cpu), Cpu, $"Policy Rule B requires 0 < Cpu <= {MaxCpu}.");
        }

        if (string.IsNullOrWhiteSpace(Memory) || !AllowedMemoryTiers.Contains(Memory.Trim(), StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(Memory), Memory, "Policy Rule B requires Memory <= 4Gi using a valid ACA Consumption tier.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "PollInterval must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(ImageOverride) && !ImageRefPattern().IsMatch(ImageOverride.Trim()))
        {
            throw new ArgumentException("AcaJob.ImageOverride is not a valid container image reference.", nameof(ImageOverride));
        }

        return new Uri($"https://{armUri.Host}/", UriKind.Absolute);
    }

    public void Validate() => _ = ValidateAndGetArmBaseUri();

    [GeneratedRegex(@"^[A-Za-z0-9._()-]{1,90}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceGroupPattern();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex JobNamePattern();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNamePattern();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}(?:-preview)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/:@-]{1,254}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRefPattern();
}
