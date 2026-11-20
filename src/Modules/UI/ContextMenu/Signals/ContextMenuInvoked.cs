using DigitalBrain;

namespace IntoChat.OS.Modules.UI.ContextMenu.Signals;

[Signal("ui.context-menu-invoked")]
public sealed record ContextMenuInvoked(string MenuId, int Version, string ItemId, string Label) : Signal;
