using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Dialog.Signals;

[Signal("ui.dialog-resolved")]
public sealed record DialogResolved(
    string DialogId,
    int Version,
    bool Confirmed,
    string? InputValue) : Signal;
