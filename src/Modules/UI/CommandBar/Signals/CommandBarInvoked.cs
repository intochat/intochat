using DigitalBrain;

namespace IntoChat.OS.Modules.UI.CommandBar.Signals;

[Signal("ui.command-bar-invoked")]
public sealed record CommandBarInvoked(string BarId, int Version, string ItemId, string Label) : Signal;
