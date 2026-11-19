using DigitalBrain;

namespace IntoChat.OS.Modules.UI.CalendarView.Signals;

[Signal("ui.calendar-changed")]
public sealed record CalendarViewChanged(string CalendarId, int Version, int Year, int Month, IReadOnlyList<string> SelectedDates) : Signal;
