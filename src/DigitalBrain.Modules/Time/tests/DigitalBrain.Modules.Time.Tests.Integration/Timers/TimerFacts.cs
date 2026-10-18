using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Testing;
using DigitalBrain.Time;
using DigitalBrain.Time.Timers.Signals;
using Orleans;
using Xunit;

namespace DigitalBrain.Modules.Time.Tests.Integration;

public sealed class TimerFacts(TimeHost host)
{
    [Trait("host", "own")]
    [Fact]
    public async Task OneShotReleasesItsRegistrationAndStopIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await StartAsync(ct, control);
        var timer = brain.Get<ITimer>("once");
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await timer.Start(TimeSpan.FromDays(1));
        var registration = control.For(timer.GetGrainId());
        Assert.Equal(TimeSpan.FromDays(1), registration.Options.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, registration.Options.Period);
        Assert.True(registration.Options.KeepAlive);
        Assert.False(registration.Options.Interleave);
        Assert.Empty(ticks.Snapshot);
        await registration.FireAsync(ct);
        Assert.Equal("once", (await ticks.NextAsync(ct: ct)).TimerId);
        Assert.True(registration.IsDisposed);
        await timer.Stop();
        await timer.Stop();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => registration.FireAsync(ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReplacementDisposesPreviousScheduleAndPeriodicTicksContinueUntilStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await StartAsync(ct, control);
        var timer = brain.Get<ITimer>("replace");
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await timer.Start(TimeSpan.FromDays(1));
        var previous = control.For(timer.GetGrainId());
        await timer.Start(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var current = control.For(timer.GetGrainId());
        Assert.True(previous.IsDisposed);
        Assert.Equal(TimeSpan.FromSeconds(3), current.Options.Period);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => previous.FireAsync(ct));
        await current.FireAsync(ct);
        await ticks.NextAsync(ct: ct);
        await current.FireAsync(ct);
        await ticks.NextAsync(ct: ct);
        Assert.False(current.IsDisposed);
        Assert.Equal(2, ticks.Snapshot.Count);
        await timer.Stop();
        Assert.True(current.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => current.FireAsync(ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InvalidReplacementPreservesTheWorkingTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await StartAsync(ct, control);
        var timer = brain.Get<ITimer>("invalid");
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await timer.Start(TimeSpan.Zero);
        var original = control.For(timer.GetGrainId());
        foreach (var due in new[] { TimeSpan.FromTicks(-1), Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
        { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => timer.Start(due)); }
        foreach (var period in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue })
        { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.Zero, period)); }
        Assert.Same(original, control.For(timer.GetGrainId()));
        await original.FireAsync(ct);
        Assert.Equal("invalid", (await ticks.NextAsync(ct: ct)).TimerId);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationLosesTheScheduleAndRequiresAnotherStart(bool restartHost)
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await StartAsync(ct, control);
        var timer = brain.Get<ITimer>("deactivate");
        await timer.Start(TimeSpan.FromDays(1));
        var original = control.For(timer.GetGrainId());
        if (restartHost)
        {
            await brain.RestartAsync(ct);
            timer = brain.Get<ITimer>("deactivate");
        }
        else { await brain.DeactivateAsync(timer, ct); }
        Assert.True(original.IsDisposed);
        // Stop activates a fresh grain but must not recreate the discarded timer.
        await timer.Stop();
        Assert.Same(original, control.For(timer.GetGrainId()));
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await timer.Start(TimeSpan.Zero);
        var current = control.For(timer.GetGrainId());
        Assert.NotSame(original, current);
        await current.FireAsync(ct);
        Assert.Equal("deactivate", (await ticks.NextAsync(ct: ct)).TimerId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TicksWithoutASubscriberAreNotReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await StartAsync(ct, control);
        var timer = brain.Get<ITimer>("live-only");
        await timer.Start(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var registration = control.For(timer.GetGrainId());
        await registration.FireAsync(ct);
        var subscribedAt = control.UtcNow += TimeSpan.FromSeconds(1);
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await registration.FireAsync(ct);
        Assert.True((await ticks.NextAsync(ct: ct)).ObservedAt >= subscribedAt);
        await timer.Stop();
        Assert.Single(ticks.Snapshot);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ObsoleteCallbacksAfterReplacementAndStopCannotPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await StartAsync(ct, control);
        var timer = brain.Get<ITimer>("obsolete");
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await timer.Start(TimeSpan.FromDays(1));
        var old = control.For(timer.GetGrainId());
        await timer.Start(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var replacement = control.For(timer.GetGrainId());
        await old.DeliverObsoleteAsync(ct);
        await timer.Stop();
        await replacement.DeliverObsoleteAsync(ct);
        var expected = control.UtcNow += TimeSpan.FromSeconds(1);
        await timer.Start(TimeSpan.Zero);
        await control.For(timer.GetGrainId()).FireAsync(ct);
        Assert.Equal(expected, (await ticks.NextAsync(ct: ct)).ObservedAt);
        Assert.Single(ticks.Snapshot);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task LongDueTimePreventsIdleCollectionAndStopOrCompletionReleasesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var brain = await TestBrain.Create().WithModule<TimeModule>()
            .ConfigureServices(services => services.UseControlledTimers(control)).ConfigureServices(services => services.UseFastCollection())
            .StartAsync(ct);
        var timer = brain.Get<ITimer>("long-delay");
        await timer.Start(TimeSpan.FromDays(1));
        var active = control.For(timer.GetGrainId());
        var sentinel = brain.Get<ITimer>("idle-sentinel");
        await sentinel.Start(TimeSpan.Zero);
        var completed = control.For(sentinel.GetGrainId());
        await completed.FireAsync(ct);
        await completed.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(active.Context.Deactivated.IsCompleted);
        await timer.Stop();
        await active.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task RealPeriodicTimerPublishesRepeatedTicks()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var timer = brain.Get<ITimer>("periodic");
        await using var ticks = await brain.Observe<TimerTick>(timer, ct);
        await timer.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        Assert.Equal("periodic", (await ticks.NextAsync(ct: ct)).TimerId);
        Assert.Equal("periodic", (await ticks.NextAsync(ct: ct)).TimerId);
        await timer.Stop();
    }

    private static Task<ITestBrain> StartAsync(CancellationToken ct, ControlledTimers timers)
        => TestBrain.Create().WithModule<TimeModule>()
            .ConfigureServices(services => services.UseControlledTimers(timers))
            .StartAsync(ct);
}
