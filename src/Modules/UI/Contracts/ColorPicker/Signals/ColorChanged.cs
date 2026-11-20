using DigitalBrain;

namespace IntoChat.OS.Modules.UI.ColorPicker.Signals;

[Signal("ui.color-changed")]
public sealed record ColorChanged(string ColorPickerId, int Version, string Hex) : Signal;
