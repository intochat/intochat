using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;

namespace DigitalBrain.Identity.Local.Tests.Unit;

// The host side a real host supplies (on Orleans: PlatformIdentityDirectoryPort): it enters the kernel for the directory's
// platform members and the session store, and passes the caller-deciding members through under the stamped caller.
internal sealed class KernelHostPort(LocalDirectoryPort inner, EdgeAuthority edge) : IIdentityDirectoryPort, IIdentitySessionPort
{
    public async Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.ReadAccount(accountId, cancellationToken); }
    }

    public async Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.OwnerOfBrain(brainId, cancellationToken); }
    }

    public async Task EnsureAccount(Account account, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { await inner.EnsureAccount(account, cancellationToken); }
    }

    public async Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.CreateBrain(accountId, principalId, operationId, displayName, kind, cancellationToken); }
    }

    public async Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.EnsureBrain(owner, cancellationToken); }
    }

    public async Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.ProvisionOwner(owner, cancellationToken); }
    }

    public async Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.IsRegistered(principalId, cancellationToken); }
    }

    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.RegisterPrincipal(principalId, displayName, cancellationToken); }
    }

    public async Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.CompleteRegistration(principalId, cancellationToken); }
    }

    public async Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.BrainsOf(principalId, cancellationToken); }
    }

    public async Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.KindOf(brainScopeId, cancellationToken); }
    }

    public async Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.BrainExists(brainScopeId, cancellationToken); }
    }

    public async Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.Membership(brainScopeId, principalId, cancellationToken); }
    }

    public async Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.ListGrants(brainScopeId, cancellationToken); }
    }

    public async Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.Issue(request, cancellationToken); }
    }

    public async Task End(string sessionId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { await inner.End(sessionId, cancellationToken); }
    }

    public async Task<Member?> Validate(string sessionId, CancellationToken cancellationToken)
    {
        using (edge.EnterKernel(BrainId.Kernel)) { return await inner.Validate(sessionId, cancellationToken); }
    }

    public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken) => inner.EnsureMember(member, cancellationToken);

    public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken) => inner.RemoveMember(brainScopeId, accountId, cancellationToken);

    public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken) => inner.RemovePrincipalMember(brainScopeId, principalId, cancellationToken);

    public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken) => inner.Authorize(request, cancellationToken);

    public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken) => inner.GrantAsOwner(grant, cancellationToken);

    public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken) => inner.RevokeAsOwner(appId, semanticTypeId, mode, cancellationToken);

    public IdentityProviderDescriptor Describe() => inner.Describe();
}
