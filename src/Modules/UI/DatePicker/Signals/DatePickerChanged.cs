using DigitalBrain;

namespace IntoChat.OS.Modules.UI.DatePicker.Signals;

[Signal("ui.date-picker-changed")]
public sealed record DatePickerChanged(string PickerId, int Version, string SelectedDate) : Signal;
