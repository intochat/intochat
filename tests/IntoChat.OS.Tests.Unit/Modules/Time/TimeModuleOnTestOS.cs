using DigitalBrain.Testing;
using IntoChat.OS.Modules.Time.Timers;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Time;

public sealed class TimeModuleOnTestOS : ModuleOnTestOSFacts<TimeModule>
{
    [Fact]
    public void TimerNeuronExposesMaxConsecutiveFailuresAndMaximumDelayBounds()
    {
        Assert.Equal(3, TimerNeuron.MaxConsecutiveFailures);
        Assert.True(TimerNeuron.MaximumDelay > TimeSpan.FromDays(40));
        Assert.Equal(TimeSpan.FromSeconds(1), TimerNeuron.DefaultMinimumProductionPeriod);
    }

    [Fact]
    public async Task TimerNeuronEnforcesConfiguredMinimumTimerPeriod()
    {
        var options = Options.Create(new TimeModuleOptions { MinimumTimerPeriod = TimeSpan.FromSeconds(5) });
        var neuron = new TimerNeuron(options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => neuron.Start(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }
}
