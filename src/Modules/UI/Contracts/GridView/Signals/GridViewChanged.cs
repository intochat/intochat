using DigitalBrain;

namespace IntoChat.OS.Modules.UI.GridView.Signals;

[Signal("ui.grid-view-changed")]
public sealed record GridViewChanged(string GridId, int Version, string? SelectedId) : Signal;
