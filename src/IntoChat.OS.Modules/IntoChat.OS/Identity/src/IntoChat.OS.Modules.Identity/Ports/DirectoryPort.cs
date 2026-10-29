using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules;
using Os = IntoChat.OS.Framework.Modules.Platform.Identity;
using OsCall = IntoChat.OS.Framework;
using OsGrant = IntoChat.OS.Connections.Identity.Grant;
using OsGrantMode = IntoChat.OS.Connections.Identity.GrantMode;

namespace IntoChat.OS.Authority;

// identity@1's directory port over the local directory. Membership changes need the brain's owner as the stamped caller.
public sealed class DirectoryPort(IIdentityClient identity) : IIdentityDirectoryPort
{
    public async Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken)
        => ToAccount(await identity.ReadAccount(accountId).WaitAsync(cancellationToken).ConfigureAwait(false));

    public async Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken)
        => ToAccount(await identity.OwnerOfBrain(brainId).WaitAsync(cancellationToken).ConfigureAwait(false));

    public Task EnsureAccount(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        return identity.EnsureAccount(new Os.Account { AccountId = account.AccountId, Name = account.Name, OwnerPrincipalId = account.OwnerPrincipalId, CreatedAt = account.CreatedAt })
            .WaitAsync(cancellationToken);
    }

    public async Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken)
        => ToProvision(await identity.CreateBrain(accountId, principalId, operationId, displayName, (Os.BrainKind)(int)kind).WaitAsync(cancellationToken).ConfigureAwait(false));

    // identity@1 EnsureBrain holds for an owner whose account is not on record yet: the account is created for that owner first,
    // as registration does (the os account refuses a brain without one).
    public async Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (await identity.ReadAccount(owner.AccountId).WaitAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            await identity.EnsureAccount(new Os.Account { AccountId = owner.AccountId, Name = owner.DisplayName, OwnerPrincipalId = owner.PrincipalId, CreatedAt = owner.JoinedAt })
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return ToProvision(await identity.EnsureBrain(ToOs(owner)).WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken)
        => ToProvision(await identity.ProvisionOwner(ToOs(owner)).WaitAsync(cancellationToken).ConfigureAwait(false));

    public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => identity.IsRegistered(principalId).WaitAsync(cancellationToken);

    // Idempotent as identity@1 requires: a principal already on record answers its own brain again, not new.
    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken)
        => await identity.CompleteRegistration(principalId).WaitAsync(cancellationToken).ConfigureAwait(false) is { } existing
            ? ToProvision(existing with { NewBrain = false })
            : ToProvision(await identity.RegisterPrincipal(principalId, displayName).WaitAsync(cancellationToken).ConfigureAwait(false));

    public async Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken)
        => await identity.CompleteRegistration(principalId).WaitAsync(cancellationToken).ConfigureAwait(false) is { } provision ? ToProvision(provision) : null;

    public async Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken)
        => [.. (await identity.BrainsOf(principalId).WaitAsync(cancellationToken).ConfigureAwait(false)).Select(entry => new BrainEntry(entry.Id, (BrainKind)(int)entry.Kind))];

    public async Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken)
        => (BrainKind)(int)await identity.KindOf(brainScopeId).WaitAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => identity.BrainExists(brainScopeId).WaitAsync(cancellationToken);

    public async Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken)
        => ToMember(await identity.Membership(brainScopeId, principalId).WaitAsync(cancellationToken).ConfigureAwait(false));

    public async Task<Member?> EnsureMember(CapabilityCaller caller, Member member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        await RequireOwner(caller, member.BrainId, cancellationToken).ConfigureAwait(false);
        return ToMember(await identity.EnsureMember(ToOs(member)).WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task RemoveMember(CapabilityCaller caller, string brainScopeId, string accountId, CancellationToken cancellationToken)
    {
        await RequireOwner(caller, brainScopeId, cancellationToken).ConfigureAwait(false);
        await identity.RemoveMember(brainScopeId, accountId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePrincipalMember(CapabilityCaller caller, string brainScopeId, string principalId, CancellationToken cancellationToken)
    {
        await RequireOwner(caller, brainScopeId, cancellationToken).ConfigureAwait(false);
        await identity.RemovePrincipalMember(brainScopeId, principalId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CallDecision?> Authorize(CapabilityCaller caller, CallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var decision = await identity.Authorize(new OsCall.CallRequest
        {
            Caller = Stamped(caller),
            TargetNeuron = request.TargetNeuron,
            Operation = request.Operation,
            SemanticTypeIds = [.. request.SemanticTypeIds],
            EstimatedCompute = request.EstimatedCompute,
            HasSideEffects = request.HasSideEffects,
            EnforceTarget = request.EnforceTarget,
            TargetScope = request.TargetScope,
            PublicOperation = request.PublicOperation,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return decision is null ? null : new CallDecision
        {
            Allowed = decision.Allowed,
            Denial = decision.Denial is { } denial ? (CallDenial)(int)denial : null,
            Explanation = decision.Explanation,
            ConsumesOnce = decision.ConsumesOnce,
        };
    }

    public async Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken)
        => [.. (await identity.ListGrants(brainScopeId).WaitAsync(cancellationToken).ConfigureAwait(false)).Select(ToGrant)];

    public async Task<Grant?> GrantAsOwner(CapabilityCaller caller, Grant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            return ToGrant(await identity.GrantAsOwner(User(caller), new OsGrant
            {
                AppId = grant.AppId,
                SemanticTypeId = grant.SemanticTypeId,
                Mode = (OsGrantMode)(int)grant.Mode,
                WorkspaceId = grant.WorkspaceId,
                ConversationId = grant.ConversationId,
                GrantedAt = grant.GrantedAt,
                Revoked = grant.Revoked,
                TargetNeuronKey = grant.TargetNeuronKey,
            }).WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<bool> RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            await identity.RevokeAsOwner(User(caller), appId, semanticTypeId, (OsGrantMode)(int)mode).WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Only the brain's owner changes its membership.
    private async Task RequireOwner(CapabilityCaller caller, string brainScopeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.AppId is null
            && await identity.Membership(brainScopeId, caller.PrincipalId).WaitAsync(cancellationToken).ConfigureAwait(false) is { Role: Os.MemberRole.Owner })
        { return; }
        throw new UnauthorizedAccessException("Only the brain's owner changes its membership.");
    }

    // Kind follows the app id, so the capability caller is already the stamp the directory stores.
    private static CapabilityCaller Stamped(CapabilityCaller caller) => caller;

    private static CapabilityCaller User(CapabilityCaller caller) => caller;

    private static Account? ToAccount(Os.Account? account) => account is null ? null : new Account
    {
        AccountId = account.AccountId,
        Name = account.Name,
        OwnerPrincipalId = account.OwnerPrincipalId,
        CreatedAt = account.CreatedAt,
    };

    private static Member? ToMember(Os.Member? member) => member is null ? null : new Member
    {
        PrincipalId = member.PrincipalId,
        AccountId = member.AccountId,
        BrainId = member.BrainId,
        Role = (MemberRole)(int)member.Role,
        DisplayName = member.DisplayName,
        JoinedAt = member.JoinedAt,
    };

    private static Os.Member ToOs(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new Os.Member
        {
            PrincipalId = member.PrincipalId,
            AccountId = member.AccountId,
            BrainId = member.BrainId,
            Role = (Os.MemberRole)(int)member.Role,
            DisplayName = member.DisplayName,
            JoinedAt = member.JoinedAt,
        };
    }

    private static BrainProvision ToProvision(Os.BrainProvision provision) => new(ToMember(provision.Member)!, provision.NewBrain);

    private static Grant ToGrant(OsGrant grant) => new()
    {
        AppId = grant.AppId,
        SemanticTypeId = grant.SemanticTypeId,
        Mode = (GrantMode)(int)grant.Mode,
        WorkspaceId = grant.WorkspaceId,
        ConversationId = grant.ConversationId,
        GrantedAt = grant.GrantedAt,
        Revoked = grant.Revoked,
        TargetNeuronKey = grant.TargetNeuronKey,
    };
}
