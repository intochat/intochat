using DigitalBrain.Testing;
using IntoChat.OS.Modules.Time.Timers;

namespace IntoChat.OS.Modules.Time;

public sealed class TimeModuleOnTestOS : ModuleOnTestOSFacts<TimeModule>
{
    [Fact]
    public void TimerNeuronExposesMaxConsecutiveFailuresAndMaximumDelayBounds()
    {
        Assert.Equal(3, TimerNeuron.MaxConsecutiveFailures);
        Assert.True(TimerNeuron.MaximumDelay > TimeSpan.FromDays(40));
    }
}
