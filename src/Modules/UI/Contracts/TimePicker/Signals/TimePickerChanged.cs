using DigitalBrain;

namespace IntoChat.OS.Modules.UI.TimePicker.Signals;

[Signal("ui.time-picker-changed")]
public sealed record TimePickerChanged(string PickerId, int Version, int Hour, int Minute) : Signal;
