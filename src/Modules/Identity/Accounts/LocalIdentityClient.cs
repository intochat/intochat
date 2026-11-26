using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

// The in-module directory facade. State lives on DirectoryNeuron; active ephemeral brains live purely in RAM in EphemeralBrainRegistry.
internal sealed class LocalIdentityClient(
    AuthorityNeurons neurons,
    EdgeAuthority? edge = null,
    IntoChat.OS.Modules.Identity.Grants.IEdgeGrantsCache? edgeCache = null,
    EphemeralBrainRegistry? ephemeral = null) : IIdentityClient
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

    public Task<Account?> ReadAccount(string accountId)
        => ephemeral is not null && ephemeral.TryGet(accountId, out var rec)
            ? Task.FromResult<Account?>(rec.Account)
            : SystemCall(() => Directory.ReadAccount(accountId));

    public Task<Account?> OwnerOfBrain(string brainId)
        => ephemeral is not null && ephemeral.TryGet(brainId, out var rec)
            ? Task.FromResult<Account?>(rec.Account)
            : SystemCall(() => Directory.OwnerOfBrain(brainId));

    public Task EnsureAccount(Account account) => SystemCall(() => Directory.EnsureAccount(account));
    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
        => SystemCall(() => Directory.CreateBrain(accountId, principalId, operationId, displayName, kind));
    public Task<BrainProvision> EnsureBrain(Member owner) => SystemCall(() => Directory.EnsureBrain(owner));
    public Task<BrainProvision> ProvisionOwner(Member owner) => SystemCall(() => Directory.ProvisionOwner(owner));
    public Task<bool> IsRegistered(string principalId) => SystemCall(() => Directory.IsRegistered(principalId));
    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => SystemCall(() => Directory.RegisterPrincipal(principalId, displayName));
    public Task<BrainProvision?> CompleteRegistration(string principalId) => SystemCall(() => Directory.CompleteRegistration(principalId));

    public Task<BrainEntry[]> BrainsOf(string principalId)
        => ephemeral is not null && ephemeral.TryGet(principalId, out var rec)
            ? Task.FromResult<BrainEntry[]>([new BrainEntry(rec.BrainId, BrainKind.Personal)])
            : SystemCall(() => Directory.BrainsOf(principalId));

    public Task<BrainKind> KindOf(string brainScopeId)
        => ephemeral is not null && ephemeral.Contains(brainScopeId)
            ? Task.FromResult(BrainKind.Personal)
            : SystemCall(() => Directory.KindOf(brainScopeId));

    public Task<bool> BrainExists(string brainScopeId)
        => ephemeral is not null && ephemeral.Contains(brainScopeId)
            ? Task.FromResult(true)
            : SystemCall(() => Directory.BrainExists(brainScopeId));

    public Task<Member?> Membership(string brainScopeId, string principalId)
        => ephemeral is not null && ephemeral.TryGet(brainScopeId, out var rec)
            ? Task.FromResult<Member?>(string.Equals(rec.Owner.PrincipalId, principalId, StringComparison.Ordinal) ? rec.Owner : null)
            : SystemCall(() => Directory.Membership(brainScopeId, principalId));

    public Task<Member?> EnsureMember(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return ephemeral is not null && ephemeral.Contains(member.BrainId)
            ? Task.FromResult<Member?>(null)
            : SystemCall(() => Directory.EnsureMember(member));
    }

    public Task RemoveMember(string brainScopeId, string accountId)
        => ephemeral is not null && ephemeral.Contains(brainScopeId)
            ? Task.CompletedTask
            : SystemCall(() => Directory.RemoveMember(brainScopeId, accountId));

    public Task RemovePrincipalMember(string brainScopeId, string principalId)
        => ephemeral is not null && ephemeral.Contains(brainScopeId)
            ? Task.CompletedTask
            : SystemCall(() => Directory.RemovePrincipalMember(brainScopeId, principalId));

    // Decided here, from the caller value, over the directory's state (or RAM registry for active ephemeral brains).
    public Task<CallDecision?> Authorize(Caller? caller, CallRequest request)
    {
        if (caller is not null && ephemeral is not null && ephemeral.TryGetAuthorityRow(caller.Brain.Value, out var row))
        {
            return BrainAuthorityDecisions.Decide(row, caller, request, edgeCache);
        }
        return BrainAuthorityDecisions.Authorize(caller, request,
            (brain, grantsBrain) => SystemCall(() => Directory.AuthorityOf(brain, grantsBrain)),
            (brain, grantsBrain, ids) => SystemCall(() => Directory.SpendOnce(brain, grantsBrain, ids)),
            edgeCache);
    }

    public Task<Grant[]> ListGrants(string brainScopeId)
        => ephemeral is not null && ephemeral.TryGet(brainScopeId, out var rec)
            ? Task.FromResult(rec.ToAuthorityRow().Grants)
            : SystemCall(() => Directory.ListGrants(brainScopeId));

    // The owner methods keep the stamped owner: no module stamp replaces it.
    public Task<Grant> GrantAsOwner(string brainId, Grant grant) => Directory.GrantAsOwner(brainId, grant);

    public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants)
        => ephemeral is not null && ephemeral.TryAddShippedGrants(brainScopeId, grants, out var added)
            ? Task.FromResult(added)
            : SystemCall(() => Directory.GrantShipped(brainScopeId, grants));

    public Task RevokeAsOwner(string brainId, string grantId) => Directory.RevokeAsOwner(brainId, grantId);
    [Obsolete("Use RevokeAsOwner with grantId")]
    public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
        => Directory.RevokeAsOwner(brainId, appId, semanticTypeId, mode);
    public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
        => SystemCall(() => Directory.RedeemExternalTicket(issuer, email, displayName));
    public Task<BrainProvision> ClaimEphemeralBrain(string brainScopeId, string principalId, string displayName, Grant[]? carriedGrants = null)
        => SystemCall(() => Directory.ClaimEphemeralBrain(brainScopeId, principalId, displayName, carriedGrants));
}
