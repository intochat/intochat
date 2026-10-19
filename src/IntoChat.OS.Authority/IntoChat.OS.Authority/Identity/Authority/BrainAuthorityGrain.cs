using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Authority.Identity.Configuration;
using IntoChat.OS.Authority.Identity.Grants;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity.Authority;

[GenerateSerializer, Alias("identity.brain-access-state.v2")]
internal sealed record BrainAuthorityState
{
    [Id(0)] public string? AccountId { get; init; }
    [Id(1)] public string? BrainId { get; init; }
    [Id(2)] public Dictionary<string, Member> Members { get; init; } = [];
    [Id(3)] public Grant[] Grants { get; init; } = [];
    [Id(4)] public Invitation[] Invitations { get; init; } = [];
    [Id(6)] public bool OwnerProvisioned { get; init; }
    [Id(7)] public BrainKind Kind { get; init; }
}

// Deliberately non-reentrant: a Once grant is checked and persisted in one turn.
[GrainType("identity.brain-access.v2")]
internal sealed class BrainAuthorityGrain(
    [PersistentState("access", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<BrainAuthorityState> storage,
    TimeProvider clock, IOptions<AuthOptions> auth, AuthorityMirror mirror) : IdentityStateGrain<BrainAuthorityState>(storage), IBrainAuthority
{
    private AuthorityNeurons Platform => new(GrainFactory);

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        mirror.Publish(this.GetPrimaryKeyString(), State);
    }

    protected override void OnPersisted() => mirror.Publish(this.GetPrimaryKeyString(), State);

    public async Task Initialize(string accountId, string brainId)
    {
        if (BrainScope.Create(accountId, brainId).Id != this.GetPrimaryKeyString()) { throw new InvalidOperationException("Brain authority key mismatch."); }
        if (State.AccountId is not null && (State.AccountId != accountId || State.BrainId != brainId))
        { throw new InvalidOperationException("Brain authority scope conflict."); }
        if (State.AccountId is null) { await Persist(State with { AccountId = accountId, BrainId = brainId }); }
    }
    public Task<string?> Account() => Task.FromResult(State.AccountId);
    public Task<BrainKind> Kind() => Task.FromResult(State.Kind);
    public Task<string[]> MemberAccounts()
        => Task.FromResult<string[]>([.. State.Members.Values.Select(member => member.AccountId).Append(State.AccountId ?? "")
            .Where(account => account.Length > 0).Distinct(StringComparer.Ordinal)]);
    public async Task Classify(BrainKind kind)
    {
        if (State.Kind == kind) { return; }
        if (State.OwnerProvisioned) { throw new InvalidOperationException($"Brain {this.GetPrimaryKeyString()} is a {State.Kind} brain; its kind is fixed."); }
        await Persist(State with { Kind = kind });
    }
    public async Task RemoveAccount(string accountId)
    {
        if (accountId == State.AccountId) { throw new InvalidOperationException($"The owner of brain {this.GetPrimaryKeyString()} cannot be removed from it."); }
        foreach (var principal in State.Members.Values.Where(member => member.AccountId == accountId).Select(member => member.PrincipalId).ToArray())
        { await RemoveMember(principal); }
    }
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
        await Platform.Principal(owner.PrincipalId).Joined(owner.BrainId, State.Kind);
        return true;
    }
    public async Task EnsureMember(Member member)
    {
        // T26-2: a circle's members are other accounts; their records keep their own account, which GrantRules matches.
        if (State.Kind == BrainKind.Circle && State.AccountId is { } owner && owner != member.AccountId)
        {
            if (member.BrainId != State.BrainId) { throw new InvalidOperationException("Brain authority scope conflict."); }
            if (member.Role == MemberRole.Owner) { throw new InvalidOperationException("A circle has one owner, its creator."); }
        }
        else { await Initialize(member.AccountId, member.BrainId); }
        if (State.Members.TryGetValue(member.PrincipalId, out var existing))
        {
            if (existing.Role != member.Role) { throw new InvalidOperationException("Membership role conflict."); }
            return;
        }
        await Persist(State with { Members = new(State.Members) { [member.PrincipalId] = member } });
        await Platform.Principal(member.PrincipalId).Joined(member.BrainId, State.Kind);
    }
    public async Task RemovePrincipal(string principalId)
    {
        if (!State.Members.TryGetValue(principalId, out var member)) { return; }
        if (member.Role == MemberRole.Owner || principalId == State.AccountId)
        { throw new InvalidOperationException($"The owner of brain {this.GetPrimaryKeyString()} cannot be removed from it."); }
        await RemoveMember(principalId);
    }
    public async Task RemoveMember(string principalId)
    {
        var members = new Dictionary<string, Member>(State.Members);
        members.Remove(principalId);
        await Persist(State with { Members = members });
        if (State.BrainId is { } brain) { await Platform.Principal(principalId).Left(brain); }
    }
    // R68: the decision is a read. The Open posture's synthetic owner is provisioned at boot or by the machine's
    // signed ProvisionOwner, never here; a Once grant is spent only by Consume.
    public Task<CallDecision?> Authorize(CallRequest request)
    {
        var decision = Decide(request);
        return Task.FromResult(decision is { Allowed: true } && GrantRules.OnceToConsume(request, State.Grants).Count > 0
            ? decision with { ConsumesOnce = true }
            : decision);
    }

    public async Task<CallDecision?> Consume(CallRequest request)
    {
        var decision = Decide(request);
        if (decision is { Allowed: true })
        {
            var consumed = GrantRules.OnceToConsume(request, State.Grants);
            if (consumed.Count > 0) { await Persist(State with { Grants = [.. State.Grants.Except(consumed)] }); }
        }
        return decision;
    }

    private CallDecision? Decide(CallRequest request) => BrainAuthorityDecisions.Decide(State, request);
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
