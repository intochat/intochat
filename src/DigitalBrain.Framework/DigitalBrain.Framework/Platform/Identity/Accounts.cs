using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Framework.Platform.Identity;

[GenerateSerializer, Alias("identity.account")]
public sealed record Account
{
    [Id(0)] public required string AccountId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string OwnerPrincipalId { get; init; }
    [Id(3)] public DateTimeOffset CreatedAt { get; init; }
}

[GenerateSerializer, Alias("identity.member")]
public sealed record Member
{
    [Id(0)] public required string PrincipalId { get; init; }
    [Id(1)] public required string AccountId { get; init; }
    [Id(2)] public required string BrainId { get; init; }
    [Id(3)] public required MemberRole Role { get; init; }
    [Id(4)] public required string DisplayName { get; init; }
    [Id(5)] public DateTimeOffset JoinedAt { get; init; }
}

// The identity front door. Identity and authorization state are held by principal, account,
// and account/brain-scoped actors.
public interface IIdentityDirectory : IGrainWithStringKey
{
    Task<Member> RegisterAsync(string principalId, string password, string displayName, CancellationToken cancellationToken = default);
    Task<Member?> AuthenticateAsync(string principalId, string password, CancellationToken cancellationToken = default);

    Task<Member?> FindMemberAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default);

    // A principal may reach a brain only when it holds a member record for it, whether it owns
    // the account or the brain was shared with it.
    Task<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default);

    // The brains the principal holds a member record for, owned or shared.
    Task<string[]> BrainsOfAsync(string principalId, CancellationToken cancellationToken = default);

    Task<Member> ShareBrainAsync(string accountId, string brainId, string principalId, string displayName, MemberRole role, CancellationToken cancellationToken = default);
}

public static class IdentityGrains
{
    public const string Directory = "intochat-identity-directory";
}
