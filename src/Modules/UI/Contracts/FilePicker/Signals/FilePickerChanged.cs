using DigitalBrain;

namespace IntoChat.OS.Modules.UI.FilePicker.Signals;

[Signal("ui.file-picker-changed")]
public sealed record FilePickerChanged(
    string PickerId,
    int Version,
    string CurrentPath,
    string? ConfirmedPath) : Signal;
