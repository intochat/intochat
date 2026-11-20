using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Tabs.Signals;

[Signal("ui.tab-changed")]
public sealed record TabChanged(
    string TabsId,
    int Version,
    string? SelectedId) : Signal;
