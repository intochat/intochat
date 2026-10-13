namespace DigitalBrain.Circles.Signals;

[GenerateSerializer, Alias("circles.circle-changed")]
public sealed record CircleChanged(
    [property: Id(0)] string Name,
    [property: Id(1)] string[] Members,
    [property: Id(2)] string[] SharedApps,
    [property: Id(3)] int ComputeSharePercent,
    [property: Id(4)] string[] Machines,
    [property: Id(5)] string[]? PreviousMembers = null) : Signal;

[GenerateSerializer, Alias("circles.circle-spoke")]
public sealed record CircleSpoke(
    [property: Id(0)] string Name,
    [property: Id(1)] string Speaker,
    [property: Id(2)] string Text,
    [property: Id(3)] long Sequence) : Signal;

// Trust revocation has committed; part rotation and the circle snapshot may still be in progress.
[GenerateSerializer, Alias("circles.membership-revoked")]
public sealed record CircleMembershipRevoked(
    [property: Id(0)] string AccountId,
    [property: Id(1)] string[] PreviousMembers) : Signal;
