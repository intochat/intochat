using IntoChat.OS;

namespace IntoChat.OS.Flutter.Card.Signals;

[GenerateSerializer, Alias("ui.card-changed")]
public sealed record CardChanged([property: Id(0)] string Name, [property: Id(1)] int Version) : Signal;
