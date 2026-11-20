using DigitalBrain;

namespace IntoChat.OS.Modules.UI.InfoBar.Signals;

[Signal("ui.info-bar-changed")]
public sealed record InfoBarChanged(string InfoBarId, int Version, bool IsOpen, string Severity) : Signal;
