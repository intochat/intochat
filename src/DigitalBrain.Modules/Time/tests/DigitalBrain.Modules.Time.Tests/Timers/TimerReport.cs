using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Time.Timers;
using DigitalBrain.Time.Timers.Signals;

public static class TimerReport
{
    public static async Task RunAsync(IDigitalBrain brain, string timerId, Func<TimerTick, Task> report, CancellationToken cancellationToken)
    {
        var timer = brain.Get<DigitalBrain.Time.Timers.ITimer>(timerId);
        await foreach (var fact in brain.On<TimerTick>(timer, cancellationToken))
        {
            await report(fact);
        }
    }
}
