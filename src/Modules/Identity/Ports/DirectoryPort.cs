using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Ports;

// identity@1's directory port over the local directory. Membership changes need the brain's owner as the stamped caller.
public sealed class DirectoryPort(
    IIdentityClient identity,
    ICallerAmbient callers,
    IntoChat.OS.Modules.Identity.Contracts.Accounts.IBrainBirths? births = null) : IIdentityDirectoryPort
{
    private Caller RequiredCaller() => callers.Current ?? throw new UnauthorizedAccessException("identity@1: the call carries no caller.");

    public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
        => identity.ReadAccount(accountId).WaitAsync(cancellationToken);

    public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
        => identity.OwnerOfBrain(brainId).WaitAsync(cancellationToken);

    public Task EnsureAccount(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        return identity.EnsureAccount(account).WaitAsync(cancellationToken);
    }

    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
        => identity.CreateBrain(accountId, principalId, operationId, displayName, kind).WaitAsync(cancellationToken);

    // identity@1 EnsureBrain holds for an owner whose account is not on record yet: the account is created for that owner first,
    // as registration does (the os account refuses a brain without one).
    public async Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (await identity.ReadAccount(owner.AccountId).WaitAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            await identity.EnsureAccount(new Account { AccountId = owner.AccountId, Name = owner.DisplayName, OwnerPrincipalId = owner.PrincipalId, CreatedAt = owner.JoinedAt })
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return await identity.EnsureBrain(owner).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
        => identity.ProvisionOwner(owner).WaitAsync(cancellationToken);

    public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => identity.IsRegistered(principalId).WaitAsync(cancellationToken);

    // Idempotent as identity@1 requires: a principal already on record answers its own brain again, not new.
    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
    {
        var provision = await identity.CompleteRegistration(principalId).WaitAsync(cancellationToken).ConfigureAwait(false) is { } existing
            ? existing with { NewBrain = false }
            : await identity.RegisterPrincipal(principalId, displayName).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (births is not null) { await births.Born(provision).WaitAsync(cancellationToken).ConfigureAwait(false); }
        return provision;
    }

    public async Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
    {
        if (await identity.CompleteRegistration(principalId).WaitAsync(cancellationToken).ConfigureAwait(false) is not { } provision) { return null; }
        if (births is not null) { await births.Born(provision).WaitAsync(cancellationToken).ConfigureAwait(false); }
        return provision;
    }

    public async Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
        => await identity.BrainsOf(principalId).WaitAsync(cancellationToken).ConfigureAwait(false);

    public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken)
        => identity.KindOf(brainScopeId).WaitAsync(cancellationToken);

    public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => identity.BrainExists(brainScopeId).WaitAsync(cancellationToken);

    public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => identity.Membership(brainScopeId, principalId).WaitAsync(cancellationToken);

    public async Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        await RequireOwner(member.BrainId, cancellationToken).ConfigureAwait(false);
        return await identity.EnsureMember(member).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken)
    {
        await RequireOwner(brainScopeId, cancellationToken).ConfigureAwait(false);
        await identity.RemoveMember(brainScopeId, accountId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken)
    {
        await RequireOwner(brainScopeId, cancellationToken).ConfigureAwait(false);
        await identity.RemovePrincipalMember(brainScopeId, principalId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await identity.Authorize(callers.Current, request).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
        => [.. (await identity.ListGrants(brainScopeId).WaitAsync(cancellationToken).ConfigureAwait(false)).Select(ToGrant)];

    public async Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var brain = RequiredCaller().Brain;
        try
        {
            var owner = grant.WorkspaceId.Length > 0 ? grant.WorkspaceId : brain.Value;
            var target = string.IsNullOrEmpty(grant.TargetNeuronKey)
                ? IntoChat.OS.Host.Primitives.AppRef.Of(brain, grant.AppId).Key
                : grant.TargetNeuronKey;
            var tuple = Grant.Tuple($"brain:{owner}", $"app:{grant.AppId}", target, grant.SemanticTypeId);
            return ToGrant(await identity.GrantAsOwner(owner, tuple).WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken)
    {
        var brain = RequiredCaller().Brain;
        try
        {
#pragma warning disable CS0618
            await identity.RevokeAsOwner(brain.Value, appId, semanticTypeId, mode).WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Only the brain's owner, as a person, changes its membership: an app, an agent or the scheduler never does.
    private async Task RequireOwner(string brainScopeId, CancellationToken cancellationToken)
    {
        if (RequiredCaller().Actor is Actor.Person person
            && await identity.Membership(brainScopeId, person.Principal).WaitAsync(cancellationToken).ConfigureAwait(false) is { Role: MemberRole.Owner })
        { return; }
        throw new UnauthorizedAccessException("Only the brain's owner changes its membership.");
    }

    private static Grant ToGrant(Grant grant)
    {
        var n = Grant.Normalize(grant);
        return new()
        {
            AppId = n.Grantee.StartsWith("app:", StringComparison.Ordinal) ? n.Grantee["app:".Length..] : n.Grantee,
            SemanticTypeId = n.Scope,
            Mode = n.Mode,
            WorkspaceId = n.Owner.StartsWith("brain:", StringComparison.Ordinal) ? n.Owner["brain:".Length..] : n.Owner,
            ConversationId = n.ConversationId,
            GrantedAt = n.GrantedAt,
            Revoked = n.Revoked,
            TargetNeuronKey = n.Target,
        };
    }
}
