using DigitalBrain;

namespace IntoChat.OS.Modules.UI.TreeView.Signals;

[Signal("ui.tree-view-changed")]
public sealed record TreeViewChanged(string TreeViewId, int Version, string? SelectedId) : Signal;
