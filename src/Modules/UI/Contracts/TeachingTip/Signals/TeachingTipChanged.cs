using DigitalBrain;

namespace IntoChat.OS.Modules.UI.TeachingTip.Signals;

[Signal("ui.teaching-tip-changed")]
public sealed record TeachingTipChanged(string TipId, int Version, bool IsOpen, int ActionCount) : Signal;
