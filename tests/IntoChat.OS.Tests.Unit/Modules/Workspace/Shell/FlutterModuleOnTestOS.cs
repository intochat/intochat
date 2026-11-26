using DigitalBrain.Testing;
using IntoChat.OS.Modules.Flutter.Compute;

namespace IntoChat.OS.Modules.Flutter;

public sealed class FlutterModuleOnTestOS : ModuleOnTestOSFacts<FlutterModule>
{
    [Fact]
    public void ComputeStateEnforcesDefaultMonthlyBudgetAndBudgetExceededFlag()
    {
        var within = new ComputeState(1_970_000L, DateTimeOffset.UtcNow, []);
        Assert.Equal(50_000_000L, within.MonthlyBudgetMicros);
        Assert.False(within.BudgetExceeded);

        var exceeded = new ComputeState(50_000_001L, DateTimeOffset.UtcNow, []);
        Assert.True(exceeded.BudgetExceeded);
    }
}
