using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Select.Signals;

[Signal("ui.select-changed")]
public sealed record SelectChanged(
    string SelectId,
    int Version,
    string? SelectedId) : Signal;
