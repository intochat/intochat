using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Dialog.Signals;

[Signal("ui.dialog-changed")]
public sealed record DialogChanged(
    string DialogId,
    int Version,
    bool IsOpen) : Signal;
