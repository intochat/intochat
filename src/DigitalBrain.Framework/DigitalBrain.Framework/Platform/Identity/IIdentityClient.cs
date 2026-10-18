using DigitalBrain.Authority.Identity;

namespace DigitalBrain.Framework.Platform.Identity;

// Account, membership and grant authority inside the brain runtime.
public interface IIdentityClient
{
    Task<Account?> ReadAccount(string accountId);

    Task<Account?> OwnerOfBrain(string brainId);

    Task EnsureAccount(Account account);

    // R125: a brain is recorded with its kind; a circle brain is created for its creator's account.
    Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind);

    Task<BrainProvision> EnsureBrain(Member owner);

    Task<BrainProvision> ProvisionOwner(Member owner);

    Task<bool> IsRegistered(string principalId);

    Task<BrainProvision> RegisterPrincipal(string principalId, string displayName);

    Task<BrainProvision?> CompleteRegistration(string principalId);

    Task<BrainEntry[]> BrainsOf(string principalId);

    Task<BrainKind> KindOf(string brainScopeId);

    // A brain exists once its owner was provisioned; KindOf answers Personal for an id nobody ever created.
    Task<bool> BrainExists(string brainScopeId);

    Task<Member?> Membership(string brainScopeId, string principalId);

    Task<Member?> EnsureMember(Member member);

    // R133: the same gate as EnsureMember, plus the brain's owner. Every membership of the account goes; the owner's
    // account is never removed.
    Task RemoveMember(string brainScopeId, string accountId);

    Task<CallDecision?> Authorize(CallRequest request);

    Task<Grant[]> ListGrants(string brainScopeId);

    Task<Grant> GrantAsOwner(CallerContext caller, Grant grant);

    Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode);
}

public enum BrainKind { Personal, Circle }

[GenerateSerializer, Alias("identity.brain-entry")]
public sealed record BrainEntry([property: Id(0)] string Id, [property: Id(1)] BrainKind Kind);

[GenerateSerializer, Alias("identity.brain-provision")]
public sealed record BrainProvision([property: Id(0)] Member Member, [property: Id(1)] bool NewBrain);
