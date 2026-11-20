using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.DatePicker.Signals;

namespace IntoChat.OS.Modules.UI.DatePicker;

[Neuron("ui.date-picker")]
internal sealed class DatePickerNeuron : UiNeuronBase<DatePickerState>, IDatePicker
{
    public async Task<DatePickerState> Configure(string label, string selectedDate, string? minDate = null, string? maxDate = null, bool disabled = false)
    {
        var normalized = NormalizeDate(selectedDate);
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            SelectedDate = ClampDate(normalized, minDate, maxDate),
            MinDate = string.IsNullOrWhiteSpace(minDate) ? null : NormalizeDate(minDate),
            MaxDate = string.IsNullOrWhiteSpace(maxDate) ? null : NormalizeDate(maxDate),
            Disabled = disabled,
        };
        await Save(next, new DatePickerChanged(Id.FullKey(), next.Version, next.SelectedDate));
        return next;
    }

    public async Task<DatePickerState> SetDate(string isoDate)
    {
        if (State.Disabled) { return State; }
        var normalized = ClampDate(NormalizeDate(isoDate), State.MinDate, State.MaxDate);
        if (string.Equals(normalized, State.SelectedDate, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, SelectedDate = normalized };
        await Save(next, new DatePickerChanged(Id.FullKey(), next.Version, next.SelectedDate));
        return next;
    }

    public Task<DatePickerState> Read() => Task.FromResult(State);

    private static string NormalizeDate(string value)
        => DateOnly.TryParse(value, out var parsed) ? parsed.ToString("yyyy-MM-dd") : "2026-11-19";

    private static string ClampDate(string value, string? min, string? max)
    {
        if (!DateOnly.TryParse(value, out var date)) { return value; }
        if (!string.IsNullOrWhiteSpace(min) && DateOnly.TryParse(min, out var minD) && date < minD) { date = minD; }
        if (!string.IsNullOrWhiteSpace(max) && DateOnly.TryParse(max, out var maxD) && date > maxD) { date = maxD; }
        return date.ToString("yyyy-MM-dd");
    }
}
