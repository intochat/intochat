using IntoChat.OS;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Time.Timers;
using IntoChat.OS.Modules.Time.Timers.Signals;

public static class TimerReport
{
    public static async Task RunAsync(IIntoChatOS brain, string timerId, Func<TimerTick, Task> report, CancellationToken cancellationToken)
    {
        var timer = brain.Get<IntoChat.OS.Modules.Time.Timers.ITimer>(timerId);
        await foreach (var fact in brain.On<TimerTick>(timer, cancellationToken))
        {
            await report(fact);
        }
    }
}
