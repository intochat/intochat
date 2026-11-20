using DigitalBrain;

namespace IntoChat.OS.Modules.UI.CheckBox.Signals;

[Signal("ui.checkbox-changed")]
public sealed record CheckBoxChanged(
    string CheckBoxId,
    int Version,
    bool IsChecked) : Signal;
