using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Connections.Platform.Identity;

namespace IntoChat.OS.Authority.Identity;

// The in-module directory facade. State lives on DirectoryNeuron; product callers use ICapabilityBroker.
internal sealed class LocalIdentityClient(AuthorityNeurons neurons, EdgeAuthority? edge = null) : IIdentityClient
{
    private IDirectoryStore Directory => neurons.Directory();

    private async Task<T> SystemCall<T>(Func<Task<T>> call)
    {
        using var _ = edge?.Enter(edge.Module("identity.directory", new BrainId("system")));
        return await call();
    }

    private async Task SystemCall(Func<Task> call)
    {
        using var _ = edge?.Enter(edge.Module("identity.directory", new BrainId("system")));
        await call();
    }

    public Task<Account?> ReadAccount(string accountId) => SystemCall(() => Directory.ReadAccount(accountId));
    public Task<Account?> OwnerOfBrain(string brainId) => SystemCall(() => Directory.OwnerOfBrain(brainId));
    public Task EnsureAccount(Account account) => SystemCall(() => Directory.EnsureAccount(account));
    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
        => SystemCall(() => Directory.CreateBrain(accountId, principalId, operationId, displayName, kind));
    public Task<BrainProvision> EnsureBrain(Member owner) => SystemCall(() => Directory.EnsureBrain(owner));
    public Task<BrainProvision> ProvisionOwner(Member owner) => SystemCall(() => Directory.ProvisionOwner(owner));
    public Task<bool> IsRegistered(string principalId) => SystemCall(() => Directory.IsRegistered(principalId));
    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => SystemCall(() => Directory.RegisterPrincipal(principalId, displayName));
    public Task<BrainProvision?> CompleteRegistration(string principalId) => SystemCall(() => Directory.CompleteRegistration(principalId));
    public Task<BrainEntry[]> BrainsOf(string principalId) => SystemCall(() => Directory.BrainsOf(principalId));
    public Task<BrainKind> KindOf(string brainScopeId) => SystemCall(() => Directory.KindOf(brainScopeId));
    public Task<bool> BrainExists(string brainScopeId) => SystemCall(() => Directory.BrainExists(brainScopeId));
    public Task<Member?> Membership(string brainScopeId, string principalId) => SystemCall(() => Directory.Membership(brainScopeId, principalId));
    public Task<Member?> EnsureMember(Member member) => SystemCall(() => Directory.EnsureMember(member));
    public Task RemoveMember(string brainScopeId, string accountId) => SystemCall(() => Directory.RemoveMember(brainScopeId, accountId));
    public Task RemovePrincipalMember(string brainScopeId, string principalId) => SystemCall(() => Directory.RemovePrincipalMember(brainScopeId, principalId));
    public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => SystemCall(() => Directory.Authorize(DirectoryAuthorizeRequest.From(caller, request)));
    public Task<Grant[]> ListGrants(string brainScopeId) => SystemCall(() => Directory.ListGrants(brainScopeId));
    public Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant) => SystemCall(() => Directory.GrantAsOwner(caller, grant));
    public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => SystemCall(() => Directory.GrantShipped(brainScopeId, grants));
    public Task RevokeAsOwner(CapabilityCaller caller, string grantId) => SystemCall(() => Directory.RevokeAsOwner(caller, grantId));
    [Obsolete("Use RevokeAsOwner with grantId")]
    public Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode)
        => SystemCall(() => Directory.RevokeAsOwner(caller, appId, semanticTypeId, mode));
    public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
        => SystemCall(() => Directory.RedeemExternalTicket(issuer, email, displayName));
}
