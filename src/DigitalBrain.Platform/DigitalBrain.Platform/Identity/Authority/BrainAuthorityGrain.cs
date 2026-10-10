using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Identity.Grants;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Identity.Authority;

[GenerateSerializer, Alias("identity.brain-access-state.v2")]
internal sealed record BrainAuthorityState
{
    [Id(0)] public string? AccountId { get; init; }
    [Id(1)] public string? BrainId { get; init; }
    [Id(2)] public Dictionary<string, Member> Members { get; init; } = [];
    [Id(3)] public Grant[] Grants { get; init; } = [];
    [Id(4)] public Invitation[] Invitations { get; init; } = [];
    [Id(6)] public bool OwnerProvisioned { get; init; }
}

// Deliberately non-reentrant: a Once grant is checked and persisted in one turn.
[GrainType("identity.brain-access.v2")]
internal sealed class BrainAuthorityGrain(
    [PersistentState("access", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<BrainAuthorityState> storage,
    TimeProvider clock, IOptions<AuthOptions> auth) : IdentityStateGrain<BrainAuthorityState>(storage), IBrainAuthority
{
    public async Task Initialize(string accountId, string brainId)
    {
        if (BrainScope.Create(accountId, brainId).Id != this.GetPrimaryKeyString()) { throw new InvalidOperationException("Brain authority key mismatch."); }
        if (State.AccountId is not null && (State.AccountId != accountId || State.BrainId != brainId))
        { throw new InvalidOperationException("Brain authority scope conflict."); }
        if (State.AccountId is null) { await Persist(State with { AccountId = accountId, BrainId = brainId }); }
    }
    public Task<string?> Account() => Task.FromResult(State.AccountId);
    public Task<Member?> Membership(string principalId) => Task.FromResult(State.Members.GetValueOrDefault(principalId));
    public async Task<bool> ProvisionOwner(Member owner)
    {
        await Initialize(owner.AccountId, owner.BrainId);
        if (owner.Role != MemberRole.Owner) { throw new ArgumentException("An owner is required."); }
        if (State.OwnerProvisioned)
        {
            // Provisioning happens once: a retry is a no-op, and a revoked owner stays revoked. A
            // different principal holding the owner role is the one thing that must not pass quietly.
            var current = State.Members.Values.FirstOrDefault(member => member.Role == MemberRole.Owner);
            if (current is null || current.PrincipalId == owner.PrincipalId) { return false; }
            throw new InvalidOperationException($"Brain {owner.BrainId} already belongs to {current.PrincipalId}; {owner.PrincipalId} cannot be provisioned as its owner.");
        }
        await Persist(State with { Members = new(State.Members) { [owner.PrincipalId] = owner }, OwnerProvisioned = true });
        await GrainFactory.GetGrain<IPrincipal>(owner.PrincipalId).Joined(owner.BrainId);
        return true;
    }
    public async Task EnsureMember(Member member)
    {
        await Initialize(member.AccountId, member.BrainId);
        if (State.Members.TryGetValue(member.PrincipalId, out var existing))
        {
            if (existing.Role != member.Role) { throw new InvalidOperationException("Membership role conflict."); }
            return;
        }
        await Persist(State with { Members = new(State.Members) { [member.PrincipalId] = member } });
        await GrainFactory.GetGrain<IPrincipal>(member.PrincipalId).Joined(member.BrainId);
    }
    public async Task RemoveMember(string principalId)
    {
        var members = new Dictionary<string, Member>(State.Members);
        members.Remove(principalId);
        await Persist(State with { Members = members });
        if (State.BrainId is { } brain) { await GrainFactory.GetGrain<IPrincipal>(principalId).Left(brain); }
    }
    public async Task<CallDecision?> Authorize(CallRequest request)
    {
        var caller = request.Caller;
        // The grain-side mirror of OpenOwnerBrainAccess at the HTTP edge: with the declared Open
        // posture, an untouched brain under the synthetic owner's account belongs to the synthetic
        // owner, whoever reaches it first. A caller is never crowned: a registered account's brain
        // is provisioned by its account before it is born, so it never arrives here untouched.
        if (State.AccountId is null && auth.Value.Posture == IdentityPosture.Open
            && caller.AccountId == AccountSession.DefaultLogin
            && BrainScope.Create(caller.AccountId, caller.BrainId).Id == this.GetPrimaryKeyString())
        {
            await ProvisionOwner(new Member
            {
                PrincipalId = AccountSession.DefaultLogin,
                AccountId = AccountSession.DefaultLogin,
                BrainId = caller.BrainId,
                Role = MemberRole.Owner,
                DisplayName = AccountSession.DefaultLogin,
            });
        }
        if (State.AccountId != caller.AccountId || State.BrainId != caller.BrainId)
        { return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target scope does not match this brain."); }
        var decision = GrantRules.Evaluate(request, State.Members.GetValueOrDefault(caller.PrincipalId), State.Grants);
        if (decision is { Allowed: true })
        {
            var consumed = GrantRules.OnceToConsume(request, State.Grants);
            if (consumed.Count > 0) { await Persist(State with { Grants = [.. State.Grants.Except(consumed)] }); }
        }
        return decision;
    }
    public async Task<Grant> GrantAsOwner(CallerContext caller, Grant grant)
    {
        await RequireOwner(caller);
        return await Grant(grant);
    }
    public async Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode)
    {
        await RequireOwner(caller);
        await Revoke(appId, semanticTypeId, mode);
    }
    private async Task RequireOwner(CallerContext caller)
    {
        if (!CallerContextStamper.IsTrusted(caller) || caller.Kind != CallerKind.User
            || BrainScope.Create(caller.AccountId, caller.BrainId).Id != this.GetPrimaryKeyString())
        { throw new UnauthorizedAccessException("Only the current brain owner can manage grants."); }
        if (auth.Value.Posture == IdentityPosture.Open && caller.PrincipalId == "owner" && caller.AccountId == "owner")
        { await Initialize(caller.AccountId, caller.BrainId); return; }
        if (State.Members.GetValueOrDefault(caller.PrincipalId)?.Role != MemberRole.Owner)
        { throw new UnauthorizedAccessException("Only the current brain owner can manage grants."); }
    }
    public async Task<Grant> Grant(Grant grant)
    {
        if (State.BrainId is null) { throw new InvalidOperationException("Initialize the brain authority before granting access."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.SemanticTypeId);
        var stored = grant with { WorkspaceId = State.BrainId, GrantedAt = clock.GetUtcNow() };
        await Persist(State with
        {
            Grants = [.. State.Grants.Where(g => g.AppId != stored.AppId || g.SemanticTypeId != stored.SemanticTypeId
            || g.Mode != stored.Mode || (g.Mode == GrantMode.ThisChat && g.ConversationId != stored.ConversationId)), stored]
        });
        return stored;
    }
    public Task Revoke(string appId, string semanticTypeId, GrantMode mode)
        => Persist(State with { Grants = [.. State.Grants.Where(g => g.AppId != appId || g.SemanticTypeId != semanticTypeId || g.Mode != mode)] });
    public Task<Grant[]> ListGrants() => Task.FromResult(State.Grants.Where(g => !g.Revoked).ToArray());
}
