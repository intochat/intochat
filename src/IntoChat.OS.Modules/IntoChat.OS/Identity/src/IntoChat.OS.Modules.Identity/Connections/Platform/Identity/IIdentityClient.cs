using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

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

    Task<CallDecision?> Authorize(CallRequest request);

    Task<Grant[]> ListGrants(string brainScopeId);

    Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant);

    Task RevokeAsOwner(CapabilityCaller caller, string grantId);

    [Obsolete("Use RevokeAsOwner with grantId")]
    Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode);
}
