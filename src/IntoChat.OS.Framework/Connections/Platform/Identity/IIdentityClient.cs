using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Connections.Platform.Identity;

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

    // Removes one non-owner member of the brain by principal id; the owner throws, an unknown principal is a no-op.
    Task RemovePrincipalMember(string brainScopeId, string principalId);

    Task<CallDecision?> Authorize(Caller? caller, CallRequest request);

    Task<Grant[]> ListGrants(string brainScopeId);

    Task<Grant> GrantAsOwner(Grant grant);

    // The platform records what a shipped app needs when it installs it; a grant the brain already holds for the same
    // grantee, target and relation is kept as it is, and nothing is ever revoked here.
    Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants);

    Task RevokeAsOwner(string grantId);

    [Obsolete("Use RevokeAsOwner with grantId")]
    Task RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode);

    Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
        => throw new NotSupportedException();
}
