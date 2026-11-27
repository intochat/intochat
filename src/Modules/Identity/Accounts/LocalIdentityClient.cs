using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Identity.Rules;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.Accounts;

// The in-module directory facade over DigitalBrain.Identity.Local.Directory.IDirectory; active ephemeral brains live purely in RAM in EphemeralBrainRegistry.
internal sealed class LocalIdentityClient(
    AuthorityNeurons neurons,
    EdgeAuthority? edge = null,
    IntoChat.OS.Modules.Identity.Grants.IEdgeGrantsCache? edgeCache = null,
    EphemeralBrainRegistry? ephemeral = null,
    IOptions<AuthOptions>? auth = null) : IIdentityClient
{
    private IDirectory Directory => neurons.Directory();

    private async Task<T> SystemCall<T>(Func<Task<T>> call)
    {
        using var _ = edge?.Enter(edge.Kernel(BrainId.Kernel));
        return await call().ConfigureAwait(false);
    }

    private async Task SystemCall(Func<Task> call)
    {
        using var _ = edge?.Enter(edge.Kernel(BrainId.Kernel));
        await call().ConfigureAwait(false);
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

    // Membership and owner-grant mutations keep the stamped owner caller: DirectoryNeuron enforces OwnedBy(caller).
    public Task<Member?> EnsureMember(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return ephemeral is not null && ephemeral.Contains(member.BrainId)
            ? Task.FromResult<Member?>(null)
            : Directory.EnsureMember(member);
    }

    public Task RemoveMember(string brainScopeId, string accountId)
        => ephemeral is not null && ephemeral.Contains(brainScopeId)
            ? Task.CompletedTask
            : Directory.RemoveMember(brainScopeId, accountId);

    public Task RemovePrincipalMember(string brainScopeId, string principalId)
        => ephemeral is not null && ephemeral.Contains(brainScopeId)
            ? Task.CompletedTask
            : Directory.RemovePrincipalMember(brainScopeId, principalId);

    // Decided here, from the caller value, over the directory's state (or RAM registry for active ephemeral brains).
    public Task<CallDecision?> Authorize(Caller? caller, CallRequest request)
    {
        if (caller is not null && ephemeral is not null && ephemeral.TryGetAuthorityRow(caller.Brain.Value, out var row))
        {
            return BrainAuthorityDecisions.Decide(row, caller, request, edgeCache);
        }

        return BrainAuthorityDecisions.Authorize(
            caller,
            request,
            async (brain, grantsBrain) =>
            {
                var snapshot = await SystemCall(() => Directory.SnapshotOf(brain)).ConfigureAwait(false);
                if (!string.Equals(grantsBrain, brain, StringComparison.Ordinal))
                {
                    var targetGrants = await SystemCall(() => Directory.ListGrants(grantsBrain)).ConfigureAwait(false);
                    snapshot = snapshot with { Grants = targetGrants };
                }

                return snapshot;
            },
            (_, grantsBrain, ids) => SystemCall(() => Directory.SpendOnce(grantsBrain, ids)),
            edgeCache);
    }

    public Task<Grant[]> ListGrants(string brainScopeId)
        => ephemeral is not null && ephemeral.TryGet(brainScopeId, out var rec)
            ? Task.FromResult(rec.ToAuthorityRow().Grants)
            : SystemCall(() => Directory.ListGrants(brainScopeId));

    public async Task<Grant> GrantAsOwner(string brainId, Grant grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(grant);
        var normalized = Grant.Normalize(grant);
        if (normalized.Target.Length > 0 && !BrainAuthorityDecisions.InBrain(normalized.Target, brainId))
        {
            throw new UnauthorizedAccessException("A brain owner grants access only to neurons in that brain.");
        }

        var stored = await Directory.GrantAsOwner(brainId, normalized).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Only the current brain owner can manage grants.");
        edgeCache?.Invalidate(brainId);
        return stored;
    }

    public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentNullException.ThrowIfNull(grants);
        if (ephemeral is not null && ephemeral.TryAddShippedGrants(brainScopeId, grants, out var added))
        {
            return Task.FromResult(added);
        }

        var assistantKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainScopeId), CallerApps.Assistant).Key;
        foreach (var grant in grants)
        {
            var normalized = Grant.Normalize(grant);
            var isAssistantGrantee = string.Equals(normalized.Grantee, $"app:{CallerApps.Assistant}", StringComparison.Ordinal)
                || string.Equals(normalized.Grantee, $"app:{assistantKey}", StringComparison.Ordinal);
            var isAppGrantee = normalized.Grantee.StartsWith("app:", StringComparison.Ordinal)
                && IntoChat.OS.Host.Primitives.AppRef.TryParse(new BrainId(brainScopeId), normalized.Grantee["app:".Length..], out _);
            if ((!isAppGrantee && !isAssistantGrantee)
                || (!string.Equals(normalized.Target, "*", StringComparison.Ordinal) && !BrainAuthorityDecisions.InBrain(normalized.Target, brainScopeId)))
            {
                throw new UnauthorizedAccessException("A shipped app is granted only neurons of the brain it is installed in.");
            }
        }

        return SystemCall(() => Directory.GrantShipped(brainScopeId, grants));
    }

    public async Task RevokeAsOwner(string brainId, string grantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        if (!await Directory.RevokeGrantAsOwner(brainId, grantId).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Only the current brain owner can manage grants.");
        }

        edgeCache?.Invalidate(brainId);
    }

    [Obsolete("Use RevokeAsOwner with grantId")]
    public async Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
    {
        if (!await Directory.RevokeAsOwner(brainId, appId, semanticTypeId, mode).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Only the current brain owner can manage grants.");
        }

        edgeCache?.Invalidate(brainId);
    }

    public async Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var issuerRule = new IssuerIsKnown(issuer, auth?.Value.TrustedIssuers ?? []);
        if (issuerRule.Broken)
        {
            throw new RuleViolationException(new Rejected("RedeemExternalTicket", [issuerRule.Name], [issuerRule.Message]));
        }

        var normalizedEmail = ExternalSignInTicket.NormalizeEmail(email) ?? email.Trim();
        var principalId = IntoChat.OS.Modules.Identity.External.ExternalSignIn.PrincipalOf(issuer, normalizedEmail);
        var name = string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim();
        return await CompleteRegistration(principalId).ConfigureAwait(false)
            ?? await RegisterPrincipal(principalId, name).ConfigureAwait(false);
    }

    public Task<BrainProvision> ClaimEphemeralBrain(string brainScopeId, string principalId, string displayName, Grant[]? carriedGrants = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        if (principalId.StartsWith(GuestSurface.BrainPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An ephemeral try identity cannot claim a brain.", nameof(principalId));
        }

        Grant[]? filtered = carriedGrants is { Length: > 0 }
            ? [.. carriedGrants.Where(g => !string.Equals(Grant.Normalize(g).Grantee, "app:" + GuestSurface.AppId, StringComparison.Ordinal))]
            : null;
        return SystemCall(() => Directory.ClaimBrain(brainScopeId, principalId, displayName, filtered));
    }
}
