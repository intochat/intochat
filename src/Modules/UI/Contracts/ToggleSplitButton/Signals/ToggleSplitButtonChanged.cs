using DigitalBrain;

namespace IntoChat.OS.Modules.UI.ToggleSplitButton.Signals;

[Signal("ui.toggle-split-button-changed")]
public sealed record ToggleSplitButtonChanged(
    string ButtonId,
    int Version,
    bool IsChecked,
    string? SelectedOptionId) : Signal;
