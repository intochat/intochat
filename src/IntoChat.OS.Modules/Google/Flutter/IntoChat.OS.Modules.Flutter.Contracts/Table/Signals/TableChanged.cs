using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Table.Signals;

[GenerateSerializer, Alias("ui.table-changed")]
public sealed record TableChanged([property: Id(0)] string Name, [property: Id(1)] int Version) : Signal;
