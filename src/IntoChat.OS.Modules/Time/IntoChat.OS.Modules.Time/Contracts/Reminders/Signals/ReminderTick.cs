using IntoChat.OS;

namespace IntoChat.OS.Modules.Time.Reminders.Signals;

[GenerateSerializer, Alias("time.reminder-tick")]
public sealed record ReminderTick(
    [property: Id(0)] string ReminderId,
    [property: Id(1)] DateTimeOffset ObservedAt) : Signal;
