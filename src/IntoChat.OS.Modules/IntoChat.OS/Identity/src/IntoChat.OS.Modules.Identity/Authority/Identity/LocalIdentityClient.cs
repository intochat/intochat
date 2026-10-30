using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Identity;

// The in-module directory facade. State lives on DirectoryNeuron; product callers use ICapabilityBroker.
internal sealed class LocalIdentityClient(AuthorityNeurons neurons) : IIdentityClient, IIdentityProvisioning
{
    private IDirectoryStore Directory => neurons.Directory();

    public Task<Account?> ReadAccount(string accountId) => Directory.ReadAccount(accountId);
    public Task<Account?> OwnerOfBrain(string brainId) => Directory.OwnerOfBrain(brainId);
    public Task EnsureAccount(Account account) => Directory.EnsureAccount(account);
    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
        => Directory.CreateBrain(accountId, principalId, operationId, displayName, kind);
    public Task<BrainProvision> EnsureBrain(Member owner) => Directory.EnsureBrain(owner);
    public Task<BrainProvision> ProvisionOwner(Member owner) => Directory.ProvisionOwner(owner);
    public Task<bool> IsRegistered(string principalId) => Directory.IsRegistered(principalId);
    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => Directory.RegisterPrincipal(principalId, displayName);
    public Task<BrainProvision?> CompleteRegistration(string principalId) => Directory.CompleteRegistration(principalId);
    public Task<BrainEntry[]> BrainsOf(string principalId) => Directory.BrainsOf(principalId);
    public Task<BrainKind> KindOf(string brainScopeId) => Directory.KindOf(brainScopeId);
    public Task<bool> BrainExists(string brainScopeId) => Directory.BrainExists(brainScopeId);
    public Task<Member?> Membership(string brainScopeId, string principalId) => Directory.Membership(brainScopeId, principalId);
    public Task<Member?> EnsureMember(Member member) => Directory.EnsureMember(member);
    public Task RemoveMember(string brainScopeId, string accountId) => Directory.RemoveMember(brainScopeId, accountId);
    public Task RemovePrincipalMember(string brainScopeId, string principalId) => Directory.RemovePrincipalMember(brainScopeId, principalId);
    public Task<CallDecision?> Authorize(CallRequest request) => Directory.Authorize(request);
    public Task<Grant[]> ListGrants(string brainScopeId) => Directory.ListGrants(brainScopeId);
    public Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant) => Directory.GrantAsOwner(caller, grant);
    public Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode)
        => Directory.RevokeAsOwner(caller, appId, semanticTypeId, mode);
}
