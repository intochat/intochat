using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.CalendarView.Signals;

namespace IntoChat.OS.Modules.UI.CalendarView;

[Neuron("ui.calendar")]
internal sealed class CalendarViewNeuron : UiNeuronBase<CalendarViewState>, ICalendarView
{
    public async Task<CalendarViewState> Configure(string title, int year, int month, IReadOnlyList<string>? selectedDates = null, bool multiSelect = false)
    {
        var y = Math.Clamp(year, 1970, 2100);
        var m = Math.Clamp(month, 1, 12);
        var dates = (selectedDates ?? []).Where(static d => DateOnly.TryParse(d, out _)).Distinct(StringComparer.Ordinal).OrderBy(static d => d, StringComparer.Ordinal).ToArray();
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "Calendar",
            Year = y,
            Month = m,
            SelectedDates = dates,
            MultiSelect = multiSelect,
        };
        await Save(next, new CalendarViewChanged(Id.FullKey(), next.Version, next.Year, next.Month, next.SelectedDates));
        return next;
    }

    public async Task<CalendarViewState> NavigateMonth(int year, int month)
    {
        var y = Math.Clamp(year, 1970, 2100);
        var m = Math.Clamp(month, 1, 12);
        if (y == State.Year && m == State.Month) { return State; }
        var next = State with { Version = State.Version + 1, Year = y, Month = m };
        await Save(next, new CalendarViewChanged(Id.FullKey(), next.Version, next.Year, next.Month, next.SelectedDates));
        return next;
    }

    public async Task<CalendarViewState> ToggleDate(string isoDate)
    {
        if (!DateOnly.TryParse(isoDate, out var parsed)) { return State; }
        var normalized = parsed.ToString("yyyy-MM-dd");
        List<string> dates;
        if (State.MultiSelect)
        {
            dates = [.. State.SelectedDates];
            if (!dates.Remove(normalized)) { dates.Add(normalized); }
            dates.Sort(StringComparer.Ordinal);
        }
        else
        {
            dates = [normalized];
        }
        var next = State with { Version = State.Version + 1, Year = parsed.Year, Month = parsed.Month, SelectedDates = [.. dates] };
        await Save(next, new CalendarViewChanged(Id.FullKey(), next.Version, next.Year, next.Month, next.SelectedDates));
        return next;
    }

    public Task<CalendarViewState> Read() => Task.FromResult(State);
}
