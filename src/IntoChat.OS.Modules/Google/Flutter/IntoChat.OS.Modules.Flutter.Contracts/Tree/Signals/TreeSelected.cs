using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Tree.Signals;

[GenerateSerializer, Alias("ui.tree-selected")]
public sealed record TreeSelected([property: Id(0)] string Name, [property: Id(1)] string SelectedId) : Signal;
