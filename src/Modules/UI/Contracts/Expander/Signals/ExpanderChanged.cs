using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Expander.Signals;

[Signal("ui.expander-changed")]
public sealed record ExpanderChanged(
    string ExpanderId,
    int Version,
    bool IsExpanded) : Signal;
