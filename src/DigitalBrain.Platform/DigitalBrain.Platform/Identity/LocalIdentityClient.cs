using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;

namespace DigitalBrain.Platform.Identity;

// Platform-only callers use the local authority; user authorization remains in the authenticated edges and grains.
internal sealed class LocalIdentityClient(IGrainFactory grains) : IIdentityClient
{
    private IAccount Account(string id) => grains.GetGrain<IAccount>(id);
    private IPrincipal Principal(string id) => grains.GetGrain<IPrincipal>(id);
    private IBrainAuthority Authority(string id) => grains.GetGrain<IBrainAuthority>(id);
    public Task<Account?> ReadAccount(string accountId) => Account(accountId).Read();
    public async Task<Account?> OwnerOfBrain(string brainId) => await Authority(brainId).Account() is { } account ? await ReadAccount(account) : null;
    public Task EnsureAccount(Account account) => Account(account.AccountId).EnsureCreated(account);
    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
        => Account(accountId).CreateBrain(principalId, operationId, displayName, kind);
    public async Task<BrainProvision> EnsureBrain(Member owner) => new(owner, await Account(owner.AccountId).EnsureBrain(owner));
    public async Task<BrainProvision> ProvisionOwner(Member owner) => new(owner, await Authority(BrainScope.Create(owner.AccountId, owner.BrainId).Id).ProvisionOwner(owner));
    public Task<bool> IsRegistered(string principalId) => Principal(principalId).IsRegistered();
    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => Principal(principalId).Register(displayName);
    public Task<BrainProvision?> CompleteRegistration(string principalId) => Principal(principalId).CompleteRegistration();
    public Task<BrainEntry[]> BrainsOf(string principalId) => Principal(principalId).Brains();
    public Task<BrainKind> KindOf(string brainScopeId) => Authority(brainScopeId).Kind();
    public async Task<bool> BrainExists(string brainScopeId) => await Authority(brainScopeId).Account() is not null;
    public Task<Member?> Membership(string brainScopeId, string principalId) => Authority(brainScopeId).Membership(principalId);
    public async Task<Member?> EnsureMember(Member member)
    {
        var authority = Authority(BrainScope.Create(member.AccountId, member.BrainId).Id);
        await authority.EnsureMember(member);
        return await authority.Membership(member.PrincipalId);
    }
    public Task RemoveMember(string brainScopeId, string accountId) => Authority(brainScopeId).RemoveAccount(accountId);
    public async Task<CallDecision?> Authorize(CallRequest request)
    {
        var authority = Authority(BrainScope.Create(request.Caller.AccountId, request.Caller.BrainId).Id);
        var decision = await authority.Authorize(request);
        return decision is { Allowed: true, ConsumesOnce: true } ? await authority.Consume(request) : decision;
    }
    public Task<Grant[]> ListGrants(string brainScopeId) => Authority(brainScopeId).ListGrants();
    public Task<Grant> GrantAsOwner(CallerContext caller, Grant grant) => Authority(BrainScope.Create(caller.AccountId, caller.BrainId).Id).GrantAsOwner(caller, grant);
    public Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode)
        => Authority(BrainScope.Create(caller.AccountId, caller.BrainId).Id).RevokeAsOwner(caller, appId, semanticTypeId, mode);
}
