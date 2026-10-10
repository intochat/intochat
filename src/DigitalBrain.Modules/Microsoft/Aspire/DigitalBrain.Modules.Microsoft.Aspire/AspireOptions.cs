using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    // How long one instance may take to come back ready during a roll before the roll goes back to the previous build.
    public TimeSpan SiloStepBudget { get; set; } = TimeSpan.FromMinutes(3);

    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SiloStepBudget, TimeSpan.Zero);
}
