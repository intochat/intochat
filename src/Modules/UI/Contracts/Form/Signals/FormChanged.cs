using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Form.Signals;

[Signal("ui.form-changed")]
public sealed record FormChanged(
    string FormId,
    int Version) : Signal;
