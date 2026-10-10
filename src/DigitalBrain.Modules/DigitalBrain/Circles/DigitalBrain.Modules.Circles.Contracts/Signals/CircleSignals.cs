namespace DigitalBrain.Circles.Signals;

[GenerateSerializer, Alias("circles.circle-changed")]
public sealed record CircleChanged(
    [property: Id(0)] string Name,
    [property: Id(1)] string[] Members,
    [property: Id(2)] string[] SharedApps,
    [property: Id(3)] int ComputeSharePercent) : Signal;

[GenerateSerializer, Alias("circles.circle-invited")]
public sealed record CircleInvited(
    [property: Id(0)] string Name,
    [property: Id(1)] string From) : Signal;

[GenerateSerializer, Alias("circles.circle-spoke")]
public sealed record CircleSpoke(
    [property: Id(0)] string Name,
    [property: Id(1)] string Speaker,
    [property: Id(2)] string Text,
    [property: Id(3)] long Sequence) : Signal;

// Published by the brain's circles index whenever any of its circle copies, invitations or chats changes,
// so one subscription on the index covers every circle the brain is in. Kind: changed, invited, spoke or left.
[GenerateSerializer, Alias("circles.circle-touched")]
public sealed record CircleTouched(
    [property: Id(0)] string Name,
    [property: Id(1)] string Owner,
    [property: Id(2)] string Kind) : Signal;
