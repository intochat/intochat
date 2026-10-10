using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Identity;

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
}
