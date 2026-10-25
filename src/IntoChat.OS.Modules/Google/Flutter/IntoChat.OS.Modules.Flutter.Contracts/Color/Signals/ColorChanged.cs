using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Color.Signals;

[GenerateSerializer, Alias("ui.color-changed")]
public sealed record ColorChanged([property: Id(0)] string Name, [property: Id(1)] string Hex) : Signal;
