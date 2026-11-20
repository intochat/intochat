using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Progress.Signals;

[Signal("ui.progress-changed")]
public sealed record ProgressChanged(string ProgressId, int Version, double Value, bool IsIndeterminate) : Signal;
