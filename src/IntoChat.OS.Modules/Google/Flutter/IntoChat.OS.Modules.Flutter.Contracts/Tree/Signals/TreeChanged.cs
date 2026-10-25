using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Tree.Signals;

[GenerateSerializer, Alias("ui.tree-changed")]
public sealed record TreeChanged([property: Id(0)] string Name, [property: Id(1)] int Version) : Signal;
