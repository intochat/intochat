using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Time.Reminders;
using IntoChat.OS.Modules.Time.Reminders.Signals;
using IntoChat.OS.Modules.Time.Timers;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Time;

public sealed class TimeModuleOnTestOS : ModuleOnTestOSFacts<TimeModule>
{
    [Fact]
    public void TimerNeuronExposesMaxConsecutiveFailuresAndMaximumDelayBounds()
    {
        Assert.Equal(3, TimerNeuron.MaxConsecutiveFailures);
        Assert.True(TimerNeuron.MaximumDelay > TimeSpan.FromDays(40));
        Assert.Equal(TimeSpan.FromSeconds(1), TimerNeuron.DefaultMinimumProductionPeriod);
        Assert.EndsWith(":Modules:time:Options:MinimumReminderPeriod", TimeModule.MinimumReminderPeriodKey, StringComparison.Ordinal);
        Assert.Equal(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("time") + ":MinimumReminderPeriod", TimeModule.MinimumReminderPeriodKey);
    }

    [Fact]
    public async Task TimerNeuronEnforcesConfiguredMinimumTimerPeriod()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var services = new ServiceCollection();
        services.Configure<TimeModuleOptions>(options => options.MinimumTimerPeriod = TimeSpan.FromSeconds(5));
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register(typeof(TimeModule).Assembly);
        var timer = os.Get<IntoChat.OS.Modules.Time.Timers.ITimer>(new NeuronId(new BrainId("owner"), "timer", "t1"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => timer.Start(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ReminderNeuronPublishesTypedNeuronIdTicksAndStopsIdempotently()
    {
        var brain = new BrainId("owner");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", brain));
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var os = TestOS.Local(o => o.Start = start)
            .Register(typeof(TimeModule).Assembly);
        var id = new NeuronId(brain, "reminder", "daily");
        var reminder = os.Get<IReminder>(id);

        await reminder.Start(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        os.Clock.Advance(TimeSpan.FromSeconds(10));
        await os.Idle();
        os.Clock.Advance(TimeSpan.FromMinutes(1));
        await os.Idle();

        await reminder.Stop();
        await reminder.Stop();
        os.Clock.Advance(TimeSpan.FromMinutes(5));
        await os.Idle();

        var ticks = os.Journal(brain).Signals(id).Select(entry => entry.Signal).OfType<ReminderTick>().ToArray();
        Assert.Equal(2, ticks.Length);
        Assert.All(ticks, tick => Assert.Equal(id, tick.ReminderId));
        Assert.Equal(start.AddSeconds(10), ticks[0].ObservedAt);
        Assert.Equal(start.AddSeconds(70), ticks[1].ObservedAt);
    }

    [Fact]
    public async Task ReminderNeuronValidatesScheduleBounds()
    {
        var brain = new BrainId("owner");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", brain));
        await using var os = TestOS.Local()
            .Register(typeof(TimeModule).Assembly);
        var reminder = os.Get<IReminder>(new NeuronId(brain, "reminder", "bounds"));

        foreach (var due in new[] { TimeSpan.FromTicks(-1), Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reminder.Start(due, TimeSpan.FromMinutes(1)));
        }

        foreach (var period in new[] { TimeSpan.Zero, Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reminder.Start(TimeSpan.Zero, period));
        }
    }
}
