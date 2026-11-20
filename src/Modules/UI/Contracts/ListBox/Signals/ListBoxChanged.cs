using DigitalBrain;

namespace IntoChat.OS.Modules.UI.ListBox.Signals;

[Signal("ui.listbox-changed")]
public sealed record ListBoxChanged(
    string ListBoxId,
    int Version,
    string? SelectedId) : Signal;
