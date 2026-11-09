using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Time;

public sealed class TimeModuleOptions : IModuleOptions
{
    // Test and local development may accelerate reminders; unset, production keeps the Orleans default.
    [Setting]
    public TimeSpan? MinimumReminderPeriod { get; set; }

    // Optional minimum interval for recurring timers; when positive, TimerNeuron.Start rejects smaller periods.
    [Setting]
    public TimeSpan? MinimumTimerPeriod { get; set; }

    // When true, TimerNeuron suppresses TimerTick publication on schedule ticks (budget/governance pause).
    [Setting]
    public bool PauseTimers { get; set; }

    // A period that is not positive is ignored, as an unset one: the Orleans default stands.
    public void Validate() { }
}
