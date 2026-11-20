using DigitalBrain;

namespace IntoChat.OS.Modules.UI.RadioButtons.Signals;

[Signal("ui.radio-buttons-changed")]
public sealed record RadioButtonsChanged(string RadioButtonsId, int Version, string? SelectedId) : Signal;
