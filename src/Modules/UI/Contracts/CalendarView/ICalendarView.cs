using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.CalendarView;

[GenerateSerializer, Alias("ui.calendar.state")]
public sealed record CalendarViewState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] int Year,
    [property: Id(3)] int Month,
    [property: Id(4)] string[] SelectedDates,
    [property: Id(5)] bool MultiSelect)
{
    public CalendarViewState() : this(0, "Calendar", 2026, 11, ["2026-11-19"], false) { }
}

[NeuronAlias("ui.calendar")]
[DefaultNeuronType("ui.calendar")]
public interface ICalendarView
{
    Task<CalendarViewState> Configure(string title, int year, int month, IReadOnlyList<string>? selectedDates = null, bool multiSelect = false);
    Task<CalendarViewState> NavigateMonth(int year, int month);
    Task<CalendarViewState> ToggleDate(string isoDate);
    [ReadOnlyCall] Task<CalendarViewState> Read();
}
