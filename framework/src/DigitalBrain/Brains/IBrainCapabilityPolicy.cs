namespace DigitalBrain.Brains;

/// <summary>
/// Capabilities gated by a brain's <see cref="BrainResidency"/>.
/// </summary>
public enum BrainCapability
{
    Desktop,
    AssistantTurn,
    Gallery,
    BuiltInApp,
    ExternalConnector,
    BackgroundAutomation,
    DurableSecrets,
    DeviceEnrollment,
}

/// <summary>
/// Result of evaluating whether a brain may exercise a <see cref="BrainCapability"/> under its current residency.
/// </summary>
public sealed record CapabilityDecision(
    bool Allowed,
    bool RequiresClaim,
    string Code,
    string Message,
    BrainCapability Capability,
    string? Resource = null,
    int? RemainingTurns = null)
{
    public const string ClaimRequiredCode = "CLAIM_REQUIRED";
    public const string TrialTurnsExhaustedCode = "TRIAL_TURNS_EXHAUSTED";

    public static CapabilityDecision Allow(BrainCapability capability, string? resource = null, int? remainingTurns = null)
        => new(true, false, "ALLOWED", string.Empty, capability, resource, remainingTurns);

    public static CapabilityDecision RequireClaim(
        BrainCapability capability,
        string message,
        string? resource = null,
        string code = ClaimRequiredCode,
        int? remainingTurns = null)
        => new(false, true, code, message, capability, resource, remainingTurns);
}

/// <summary>
/// Thrown when an ephemeral brain attempts an operation that requires claiming the brain into persistent storage.
/// </summary>
public sealed class ClaimRequiredException : InvalidOperationException
{
    public CapabilityDecision Decision { get; }

    public ClaimRequiredException(CapabilityDecision decision)
        : base(decision?.Message ?? "Sign in to claim this brain and unlock this capability.")
    {
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
    }
}

/// <summary>
/// Evaluates what capabilities a brain may use based on its <see cref="BrainResidency"/>.
/// </summary>
public interface IBrainCapabilityPolicy
{
    CapabilityDecision Evaluate(BrainResidency residency, BrainCapability capability, string? resource = null, int turnsUsed = 0);

    ValueTask<CapabilityDecision> EvaluateAsync(string brainId, BrainCapability capability, string? resource = null, CancellationToken ct = default);
}

/// <summary>
/// Tracks active ephemeral brains in RAM, enforces trial turn limits, and exposes residency snapshots.
/// </summary>
public interface IBrainResidencyRegistry
{
    bool TryGetResidency(string brainId, out BrainResidency residency);

    bool TryGetEphemeral(string brainId, out BrainResidency.Ephemeral residency);

    BrainResidencySnapshot GetSnapshot(string brainId);

    void Touch(string brainId);

    bool TryConsumeTurn(string brainId, out int remainingTurns);

    int GetTurnsUsed(string brainId);

    void MarkPromoted(string brainId, string ownerPrincipalId);
}

/// <summary>
/// Storage-level operations for promoting an ephemeral in-memory brain log into durable storage or evicting it from RAM.
/// </summary>
public interface IBrainResidencyStorage
{
    Task PromoteAsync(string brainId, CancellationToken ct = default);

    Task EvictAsync(string brainId, CancellationToken ct = default);

    long GetMemoryUsedBytes(string brainId);
}

/// <summary>
/// Default residency capability policy:
/// Ephemeral brains get full Desktop, Gallery, built-in/mini-apps in RAM, and up to <see cref="BrainResidency.Ephemeral.MaxTrialTurns"/> assistant turns.
/// External OAuth connectors, background automations/schedules, durable secrets, and device enrollment return a clean claim prompt.
/// </summary>
public sealed class DefaultBrainCapabilityPolicy : IBrainCapabilityPolicy
{
    private readonly IBrainResidencyRegistry? registry;

    public DefaultBrainCapabilityPolicy(IBrainResidencyRegistry? registry = null)
    {
        this.registry = registry;
    }

    public CapabilityDecision Evaluate(BrainResidency residency, BrainCapability capability, string? resource = null, int turnsUsed = 0)
    {
        ArgumentNullException.ThrowIfNull(residency);
        if (residency is BrainResidency.Persistent)
        {
            return CapabilityDecision.Allow(capability, resource);
        }

        var ephemeral = (BrainResidency.Ephemeral)residency;
        return capability switch
        {
            BrainCapability.Desktop => CapabilityDecision.Allow(capability, resource),
            BrainCapability.Gallery => CapabilityDecision.Allow(capability, resource),
            BrainCapability.BuiltInApp => CapabilityDecision.Allow(capability, resource),
            BrainCapability.AssistantTurn => EvaluateAssistantTurn(ephemeral, resource, turnsUsed),
            BrainCapability.ExternalConnector => CapabilityDecision.RequireClaim(
                capability,
                string.IsNullOrWhiteSpace(resource)
                    ? "Sign in to claim your workspace and connect external OAuth accounts."
                    : $"Sign in to claim your workspace and connect {resource}.",
                resource),
            BrainCapability.BackgroundAutomation => CapabilityDecision.RequireClaim(
                capability,
                "Sign in to claim your workspace and run background schedules or automations.",
                resource),
            BrainCapability.DurableSecrets => CapabilityDecision.RequireClaim(
                capability,
                "Sign in to claim your workspace and store durable secrets in your vault.",
                resource),
            BrainCapability.DeviceEnrollment => CapabilityDecision.RequireClaim(
                capability,
                "Sign in to claim your workspace and link devices.",
                resource),
            _ => CapabilityDecision.RequireClaim(
                capability,
                "Sign in to claim your workspace to unlock this capability.",
                resource),
        };
    }

    public ValueTask<CapabilityDecision> EvaluateAsync(string brainId, BrainCapability capability, string? resource = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        if (registry is not null && registry.TryGetEphemeral(brainId, out var ephemeral))
        {
            var turnsUsed = registry.GetTurnsUsed(brainId);
            return ValueTask.FromResult(Evaluate(ephemeral, capability, resource, turnsUsed));
        }

        return ValueTask.FromResult(CapabilityDecision.Allow(capability, resource));
    }

    private static CapabilityDecision EvaluateAssistantTurn(BrainResidency.Ephemeral ephemeral, string? resource, int turnsUsed)
    {
        var remaining = Math.Max(0, ephemeral.MaxTrialTurns - turnsUsed);
        if (turnsUsed >= ephemeral.MaxTrialTurns)
        {
            return CapabilityDecision.RequireClaim(
                BrainCapability.AssistantTurn,
                $"You've reached the {ephemeral.MaxTrialTurns}-turn trial limit. Sign in to claim this workspace and continue your conversation without losing anything.",
                resource,
                CapabilityDecision.TrialTurnsExhaustedCode,
                remainingTurns: 0);
        }

        return CapabilityDecision.Allow(BrainCapability.AssistantTurn, resource, remaining);
    }
}
