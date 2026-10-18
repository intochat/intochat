using IntoChat.OS.Runtime;
using IntoChat.OS.Runtime.InMemory;
using IntoChat.OS.Time;
using IntoChat.OS.Time.Timers;
using IntoChat.OS.Time.Timers.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ITimer = IntoChat.OS.Time.Timers.ITimer;

namespace IntoChat.OS.Modules.Time.Tests.Unit;

// The timer contract on the in-memory runtime: ticks come from the injected clock, so each fact advances a
// FakeTimeProvider instead of waiting. The Orleans path keeps its integration facts.
public sealed class TimerFacts : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryRuntime _runtime;

    public TimerFacts() => _runtime = InMemoryRuntime.Create(_time, runtime =>
    {
        runtime.Services.AddKeyedSingleton<TimeProvider>(typeof(TimeModule), _time);
        runtime.AddNeuron<TimerNeuron>();
    });

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();

    [Fact]
    public async Task AOneShotTimerPublishesOneTickAtItsDueTime()
    {
        var timer = _runtime.Resolve<ITimer>(new BrainId("brain-1"), "timers/once");
        await timer.Start(TimeSpan.FromMinutes(5));

        await Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(await Ticks(timer));
        await Advance(TimeSpan.FromMinutes(1));
        await Advance(TimeSpan.FromMinutes(10));

        var tick = Assert.Single(await Ticks(timer));
        Assert.Equal("brain-1/timers/once", tick.TimerId);
        Assert.Equal(Start.AddMinutes(5), tick.ObservedAt);
        Assert.Equal("timer/brain-1/timers/once", tick.Publisher.Name);
    }

    [Fact]
    public async Task APeriodicTimerTicksEveryPeriodUntilStopped()
    {
        var timer = _runtime.ResolveKey<ITimer>("brain-1/timers/periodic");
        await timer.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

        await Advance(TimeSpan.FromSeconds(10));
        await Advance(TimeSpan.FromSeconds(30));
        await Advance(TimeSpan.FromSeconds(30));
        await timer.Stop();
        await Advance(TimeSpan.FromMinutes(5));

        Assert.Equal([Start.AddSeconds(10), Start.AddSeconds(40), Start.AddSeconds(70)], (await Ticks(timer)).Select(tick => tick.ObservedAt));
    }

    [Fact]
    public async Task StartingAgainReplacesTheSchedule()
    {
        var timer = _runtime.ResolveKey<ITimer>("brain-1/timers/replaced");
        await timer.Start(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        await timer.Start(TimeSpan.FromMinutes(3));

        await Advance(TimeSpan.FromMinutes(1));
        await Advance(TimeSpan.FromMinutes(1));
        await Advance(TimeSpan.FromMinutes(1));
        await Advance(TimeSpan.FromMinutes(5));

        Assert.Equal([Start.AddMinutes(3)], (await Ticks(timer)).Select(tick => tick.ObservedAt));
    }

    [Fact]
    public async Task InvalidSchedulesAreRefused()
    {
        var timer = _runtime.ResolveKey<ITimer>("brain-1/timers/invalid");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.FromSeconds(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.Zero, TimeSpan.Zero));
        await Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await Ticks(timer));
    }

    private async Task Advance(TimeSpan by)
    {
        _time.Advance(by);
        await _runtime.Idle();
    }

    private static async Task<TimerTick[]> Ticks(ITimer timer) => [.. (await timer.Read(0, 100)).Signals.Cast<TimerTick>()];
}
