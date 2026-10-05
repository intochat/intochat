namespace DigitalBrain.Apps.Signals;

// Renamed to AppChanged in the deletion sweep.
[GenerateSerializer, Alias("apps.app-changed2")]
public sealed record AppChangedV2(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version,
    [property: Id(2)] AppStatusV2 Status) : Signal;
