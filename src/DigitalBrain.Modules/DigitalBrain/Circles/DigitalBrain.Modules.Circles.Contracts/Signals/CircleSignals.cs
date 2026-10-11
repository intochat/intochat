namespace DigitalBrain.Circles.Signals;

[GenerateSerializer, Alias("circles.circle-changed")]
public sealed record CircleChanged(
    [property: Id(0)] string Name,
    [property: Id(1)] string[] Members,
    [property: Id(2)] string[] SharedApps,
    [property: Id(3)] int ComputeSharePercent,
    [property: Id(4)] string[] Machines) : Signal;

[GenerateSerializer, Alias("circles.circle-spoke")]
public sealed record CircleSpoke(
    [property: Id(0)] string Name,
    [property: Id(1)] string Speaker,
    [property: Id(2)] string Text,
    [property: Id(3)] long Sequence) : Signal;
