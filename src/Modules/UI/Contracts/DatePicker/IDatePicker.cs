using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.DatePicker;

[GenerateSerializer, Alias("ui.date-picker.state")]
public sealed record DatePickerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] string SelectedDate,
    [property: Id(3)] string? MinDate,
    [property: Id(4)] string? MaxDate,
    [property: Id(5)] bool Disabled)
{
    public DatePickerState() : this(0, "Date", "2026-11-19", null, null, false) { }
}

[NeuronAlias("ui.date-picker")]
[DefaultNeuronType("ui.date-picker")]
public interface IDatePicker
{
    Task<DatePickerState> Configure(string label, string selectedDate, string? minDate = null, string? maxDate = null, bool disabled = false);
    Task<DatePickerState> SetDate(string isoDate);
    [ReadOnlyCall] Task<DatePickerState> Read();
}
