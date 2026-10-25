using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using IntoChat.OS.Modules.Time.Timers.Signals;
using Microsoft.Extensions.DependencyInjection;
using DeactivationCause = DigitalBrain.DeactivationCause;
using IDeactivatingNeuron = DigitalBrain.Spi.IDeactivatingNeuron;

namespace IntoChat.OS.Modules.Time.Timers;

[GrainType("timer")]
internal sealed class TimerNeuron([FromKeyedServices(typeof(TimeModule))] TimeProvider time) : Neuron, ITimer, IDeactivatingNeuron
{
    private const string Schedule = "schedule";
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private IDisposable? _timer;
    private object? _schedule;

    public Task Start(TimeSpan dueTime, TimeSpan? period = null)
    {
        if (dueTime < TimeSpan.Zero || dueTime > MaximumDelay)
        { throw new ArgumentOutOfRangeException(nameof(dueTime)); }
        if (period is { } interval && (interval <= TimeSpan.Zero || interval > MaximumDelay))
        { throw new ArgumentOutOfRangeException(nameof(period)); }

        var schedule = new object();
        // A named timer replaces the previous schedule; it does not interleave and keeps the activation alive.
        _timer = Context.Timers.Start(Schedule, dueTime, period, ct => TickAsync(schedule, period is null, ct), keepAlive: true);
        _schedule = schedule;
        // Ticks extend the lifetime; a long initial delay also needs protection.
        Context.KeepAlive(Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        _schedule = null;
        _timer?.Dispose();
        _timer = null;
        Context.KeepAlive(null);
        return Task.CompletedTask;
    }

    private async Task TickAsync(object schedule, bool once, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !ReferenceEquals(_schedule, schedule)) { return; }
        if (once) { await Stop(); }
        await PublishAsync(new TimerTick(Context.Key, time.GetUtcNow()));
    }

    public Task OnDeactivating(DeactivationCause cause, CancellationToken cancellationToken)
    {
        _schedule = null;
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }
}
