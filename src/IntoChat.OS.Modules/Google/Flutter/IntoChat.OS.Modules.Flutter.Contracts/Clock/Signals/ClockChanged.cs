using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Clock.Signals;

[GenerateSerializer, Alias("ui.clock-changed")]
public sealed record ClockChanged([property: Id(0)] string Name, [property: Id(1)] int Version) : Signal;
