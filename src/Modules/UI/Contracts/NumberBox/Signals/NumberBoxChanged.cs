using DigitalBrain;

namespace IntoChat.OS.Modules.UI.NumberBox.Signals;

[Signal("ui.number-box-changed")]
public sealed record NumberBoxChanged(string NumberBoxId, int Version, double Value) : Signal;
