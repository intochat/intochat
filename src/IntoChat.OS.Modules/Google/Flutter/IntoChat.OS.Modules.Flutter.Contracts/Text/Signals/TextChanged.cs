using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Text.Signals;

[GenerateSerializer, Alias("ui.text-changed")]
public sealed record TextChanged([property: Id(0)] string Name) : Signal;
