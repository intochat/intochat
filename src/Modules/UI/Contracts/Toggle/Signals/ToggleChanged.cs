using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Toggle.Signals;

[Signal("ui.toggle-changed")]
public sealed record ToggleChanged(
    string ToggleId,
    int Version,
    bool IsOn) : Signal;
