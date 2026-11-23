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

    // How long a restart asked by a caller who is not a person waits for the person's answer before it expires.
    public TimeSpan RestartConfirmationExpiry { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SiloStepBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClusterSampling, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RestartConfirmationExpiry, TimeSpan.Zero);
        // The framework caps a plan that needs a person at seven days; a longer value would be capped silently, so it is refused.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RestartConfirmationExpiry, TimeSpan.FromDays(7));
        if (!Enum.IsDefined(CandidateHostMode))
        {
            throw new ArgumentOutOfRangeException(nameof(CandidateHostMode), CandidateHostMode, "Unknown CandidateHostMode.");
        }
    }
}
