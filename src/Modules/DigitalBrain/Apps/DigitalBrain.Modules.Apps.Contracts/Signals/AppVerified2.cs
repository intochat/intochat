namespace DigitalBrain.Apps.Signals;

// Renamed to AppVerified in the deletion sweep.
[GenerateSerializer, Alias("apps.app-verified2")]
public sealed record AppVerifiedV2(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version,
    [property: Id(2)] bool Green) : Signal;
