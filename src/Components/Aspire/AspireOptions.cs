using DigitalBrain.Modules.Options;

namespace IntoChat.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    // How long one instance may take to come back ready during a roll before the roll goes back to the previous build.
    public TimeSpan SiloStepBudget { get; set; } = TimeSpan.FromMinutes(3);

    // Retained for configuration compatibility. Membership is now delivered by Orleans'
    // change stream; no deployment timer uses this former polling interval.
    public TimeSpan ClusterSampling { get; set; } = TimeSpan.FromSeconds(5);

    // Selects whether candidate verification runs in Docker containers or as isolated local headless OS processes.
    public CandidateHostMode CandidateHostMode { get; set; } = CandidateHostMode.Docker;

    // How long a command asked by a caller who is not a person waits for the person's answer before it expires.
    public TimeSpan CommandConfirmationExpiry { get; set; } = TimeSpan.FromMinutes(5);

    // Commands that a non-person may never request (refused as "person-only" with no confirmation card created).
    public string[] PersonOnlyCommands { get; set; } = ["reset-state"];

    // Bounded wait for a command executed on the AppHost bridge.
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);

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
    }
}
