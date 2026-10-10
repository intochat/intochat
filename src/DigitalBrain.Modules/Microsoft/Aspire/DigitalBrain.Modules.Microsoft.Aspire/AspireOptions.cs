using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    // How long one instance may take to come back ready during a roll before the roll goes back to the previous build.
    public TimeSpan SiloStepBudget { get; set; } = TimeSpan.FromMinutes(3);

    // How often the cluster reads its silos; it publishes DeploymentChanged only when what it read differs from the last one.
    public TimeSpan ClusterSampling { get; set; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SiloStepBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClusterSampling, TimeSpan.Zero);
    }
}
