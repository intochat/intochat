using DigitalBrain;

namespace IntoChat.OS.Modules.UI.PropertyGrid.Signals;

[Signal("ui.property-grid-changed")]
public sealed record PropertyGridChanged(string GridId, int Version, string? SelectedPath) : Signal;
