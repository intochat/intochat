namespace IntoChat.OS.Modules.Circles.Signals;

[DigitalBrain.Signal("circles.circle-changed")]
public sealed record CircleChanged(
    string Name,
    string[] Members,
    string[] SharedApps,
    int ComputeSharePercent,
    string[]? PreviousMembers = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("circles.circle-spoke")]
public sealed record CircleSpoke(
    string Name,
    string Speaker,
    string Text,
    long Sequence) : DigitalBrain.Signal;

// Membership revocation has committed; the circle snapshot may still be in progress.
[DigitalBrain.Signal("circles.membership-revoked")]
public sealed record CircleMembershipRevoked(
    string AccountId,
    string[] PreviousMembers) : DigitalBrain.Signal;

// The circle's room (the shipped circleroom unit) hears these: a member joined the circle, or left or was removed.
[DigitalBrain.Signal("circleroom.join")]
public sealed record CircleRoomJoined(string Member) : DigitalBrain.Signal;

[DigitalBrain.Signal("circleroom.leave")]
public sealed record CircleRoomLeft(string Member) : DigitalBrain.Signal;
