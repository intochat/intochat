namespace DigitalBrain.Apps.Signals;

[GenerateSerializer, Alias("apps.app-verified2")]
public sealed record AppVerified(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version,
    [property: Id(2)] bool Green) : Signal;
