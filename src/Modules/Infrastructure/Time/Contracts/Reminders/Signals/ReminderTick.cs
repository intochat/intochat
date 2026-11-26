using DigitalBrain;

namespace IntoChat.OS.Modules.Time.Reminders.Signals;

[Signal("time.reminder-tick")]
public sealed record ReminderTick(
    NeuronId ReminderId,
    DateTimeOffset ObservedAt) : Signal;
