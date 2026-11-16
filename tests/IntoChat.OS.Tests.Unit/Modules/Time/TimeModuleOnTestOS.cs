using DigitalBrain.Testing;
using IntoChat.OS.Modules.Time.Timers;
using Microsoft.Extensions.DependencyInjection;
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
        var services = new ServiceCollection();
        services.Configure<TimeModuleOptions>(options => options.MinimumTimerPeriod = TimeSpan.FromSeconds(5));
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register(typeof(TimeModule).Assembly);
        var timer = os.Get<IntoChat.OS.Modules.Time.Timers.ITimer>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("owner"), "timer", "t1"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => timer.Start(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }
}
