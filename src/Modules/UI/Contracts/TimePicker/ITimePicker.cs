using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.TimePicker;

[GenerateSerializer, Alias("ui.time-picker.state")]
public sealed record TimePickerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] int Hour,
    [property: Id(3)] int Minute,
    [property: Id(4)] bool Use24Hour,
    [property: Id(5)] bool Disabled)
{
    public TimePickerState() : this(0, "Time", 9, 0, true, false) { }
}

[NeuronAlias("ui.time-picker")]
[DefaultNeuronType("ui.time-picker")]
public interface ITimePicker
{
    Task<TimePickerState> Configure(string label, int hour, int minute, bool use24Hour = true, bool disabled = false);
    Task<TimePickerState> SetTime(int hour, int minute);
    [ReadOnlyCall] Task<TimePickerState> Read();
}
