using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Modules.Identity.Contracts.Authority;

[GenerateSerializer, Alias("identity.invitation")]
public sealed record Invitation
{
    [Id(0)] public required string Code { get; init; }
    [Id(1)] public required string AccountId { get; init; }
    [Id(2)] public required string WorkspaceId { get; init; }
    [Id(3)] public required MemberRole Role { get; init; }
    [Id(4)] public string? Email { get; init; }
    [Id(5)] public DateTimeOffset CreatedAt { get; init; }
    [Id(6)] public bool Accepted { get; init; }
    // D-10 (additive): expiry, machine binding and the code held only as an HMAC. A workspace invite keeps Code;
    // a circle machine invite leaves Code empty and keeps CodeHash.
    [Id(7)] public DateTimeOffset? ExpiresAt { get; init; }
    [Id(8)] public string? MachineId { get; init; }
    // Base64url SHA-256 of the machine key's SubjectPublicKeyInfo.
    [Id(9)] public string? MachineKeyThumbprint { get; init; }
    [Id(10)] public DateTimeOffset? AcceptedAt { get; init; }
    [Id(11)] public string? IssuedBy { get; init; }
    [Id(12)] public InvitationKind Kind { get; init; }
    [Id(13)] public string? CodeHash { get; init; }
}

[GenerateSerializer, Alias("identity.invitation-kind")]
public enum InvitationKind
{
    Workspace = 0,
    CircleMachine = 1,
}
