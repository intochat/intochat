using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity.Authority;

// Directory state for every account, principal and brain. One plain neuron in the system brain (clean break:
// the account, principal and brain-authority grains no longer hold it).
[NeuronAlias("identity.directory"), DefaultNeuronType(IDirectoryStore.Type)]
internal interface IDirectoryStore
{
    const string Type = "identity.directory";
    const string Key = "system/main";

    Task<Account?> ReadAccount(string accountId);
    Task<Account?> OwnerOfBrain(string brainId);
    Task EnsureAccount(Account account);
    Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind);
    Task<BrainProvision> EnsureBrain(Member owner);
    Task<BrainProvision> ProvisionOwner(Member owner);
    Task<bool> IsRegistered(string principalId);
    Task<BrainProvision> RegisterPrincipal(string principalId, string displayName);
    Task<BrainProvision?> CompleteRegistration(string principalId);
    Task<BrainEntry[]> BrainsOf(string principalId);
    Task<BrainKind> KindOf(string brainScopeId);
    Task<bool> BrainExists(string brainScopeId);
    Task<Member?> Membership(string brainScopeId, string principalId);
    Task<Member?> EnsureMember(Member member);
    Task RemoveMember(string brainScopeId, string accountId);
    Task RemovePrincipalMember(string brainScopeId, string principalId);
    Task<CallDecision?> Authorize(DirectoryAuthorizeRequest request);
    Task<Grant[]> ListGrants(string brainScopeId);
    Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant);
    Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants);
    Task RevokeAsOwner(CapabilityCaller caller, string grantId);
    Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode);
    Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null);
}

internal sealed record DirectoryAuthorizeRequest(
    CapabilityCaller? Caller,
    string TargetNeuron,
    string Operation,
    IReadOnlyList<string> SemanticTypeIds,
    decimal EstimatedCompute,
    bool HasSideEffects,
    bool EnforceTarget,
    string? TargetScope,
    bool PublicOperation,
    bool IsModule = false)
{
    public static DirectoryAuthorizeRequest From(Caller? caller, CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = caller is null ? (CallerIdentity?)null : CallerIdentity.From(caller);
        return new DirectoryAuthorizeRequest(
            identity?.ToCapabilityCaller(),
            request.TargetNeuron,
            request.Operation,
            request.SemanticTypeIds,
            request.EstimatedCompute,
            request.HasSideEffects,
            request.EnforceTarget,
            request.TargetScope,
            request.PublicOperation,
            identity?.IsModule ?? false);
    }

    internal CallerIdentity? ToIdentity()
        => Caller is null ? null : CallerIdentity.From(Caller) with { IsModule = IsModule };
}

[DigitalBrain.Signal("identity.directory-changed")]
internal sealed record DirectoryChanged(string Scope) : DigitalBrain.Signal;

internal sealed record PrincipalRow
{
    public string PrincipalId { get; init; } = "";
    public Member? DefaultMembership { get; init; }
    public bool RegistrationComplete { get; init; }
    public BrainEntry[] Brains { get; init; } = [];
}

internal sealed record BrainOperation
{
    public string AccountId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public required Member Member { get; init; }
}

internal sealed record BrainRow
{
    public string BrainId { get; init; } = "";
    public string? AccountId { get; init; }
    public Member[] Members { get; init; } = [];
    public Grant[] Grants { get; init; } = [];
    public bool OwnerProvisioned { get; init; }
    public BrainKind Kind { get; init; }
}

internal sealed record DirectoryState
{
    public Account[] Accounts { get; init; } = [];
    public PrincipalRow[] Principals { get; init; } = [];
    public BrainRow[] Brains { get; init; } = [];
    public BrainOperation[] Operations { get; init; } = [];
}

[Neuron(IDirectoryStore.Type)]
internal sealed class DirectoryNeuron(TimeProvider clock, IOptions<AuthOptions> auth, AuthorityMirror mirror, IServiceProvider? services = null) : Neuron<DirectoryState>, IDirectoryStore
{
    public static IDirectoryStore Open(IServiceProvider services) => SiloNeurons.Get<IDirectoryStore>(services, IDirectoryStore.Key);

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        PublishMirror();
        return Task.CompletedTask;
    }

    protected override void OnCommitted() => PublishMirror();

    public Task<Account?> ReadAccount(string accountId) => Task.FromResult(AccountOf(State, accountId));

    public Task<Account?> OwnerOfBrain(string brainId)
        => Task.FromResult(BrainOf(State, brainId)?.AccountId is { } account ? AccountOf(State, account) : null);

    public async Task EnsureAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (AccountOf(State, account.AccountId) is { } existing)
        {
            await Check("EnsureAccount", new AccountOwnerMatches(existing.OwnerPrincipalId, account.OwnerPrincipalId));
            return;
        }
        await Commit(State with { Accounts = [.. State.Accounts, account] }, account.AccountId);
    }

    public async Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
    {
        if (AccountOf(State, accountId)?.OwnerPrincipalId != principalId) { throw new UnauthorizedAccessException("Only the account owner can create a brain."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var next = State;
        if (OperationOf(next, accountId, operationId) is not { } member)
        {
            member = new Member
            {
                PrincipalId = principalId,
                AccountId = accountId,
                BrainId = (kind == BrainKind.Circle ? "circle-" : "workspace-") + Guid.NewGuid().ToString("N"),
                DisplayName = displayName,
                Role = MemberRole.Owner,
                JoinedAt = clock.GetUtcNow(),
            };
            next = next with { Operations = [.. next.Operations, new BrainOperation { AccountId = accountId, OperationId = operationId, Member = member }] };
        }
        next = await Classify("CreateBrain", next, member.BrainId, kind);
        var (provisioned, created) = await Provision("CreateBrain", next, member);
        await Commit(provisioned, member.BrainId);
        return new BrainProvision(member, created);
    }

    public async Task<BrainProvision> EnsureBrain(Member owner)
    {
        var (next, created) = await Provision("EnsureBrain", State, owner);
        await Commit(next, owner.BrainId);
        return new BrainProvision(owner, created);
    }

    public async Task<BrainProvision> ProvisionOwner(Member owner)
    {
        var (next, created) = await Provision("ProvisionOwner", State, owner);
        await Commit(next, owner.BrainId);
        return new BrainProvision(owner, created);
    }

    public Task<bool> IsRegistered(string principalId) => Task.FromResult(PrincipalOf(State, principalId)?.RegistrationComplete == true);

    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName)
    {
        principalId ??= "";
        await Check("RegisterPrincipal",
            new PrincipalIdIsValid(principalId),
            new PrincipalIdIsAvailable(principalId, PrincipalOf(State, principalId)));
        var accountId = Guid.NewGuid().ToString("N");
        var member = new Member
        {
            PrincipalId = principalId,
            AccountId = accountId,
            BrainId = "account-" + accountId,
            DisplayName = displayName,
            Role = MemberRole.Owner,
            JoinedAt = clock.GetUtcNow(),
        };
        var next = UpsertPrincipal(State, new PrincipalRow { PrincipalId = principalId, DefaultMembership = member });
        var (completed, provision) = await FinishRegistration("RegisterPrincipal", next, principalId);
        await Commit(completed, member.BrainId);
        return provision!;
    }

    public async Task<BrainProvision?> CompleteRegistration(string principalId)
    {
        if (PrincipalOf(State, principalId)?.DefaultMembership is null) { return null; }
        var (next, provision) = await FinishRegistration("CompleteRegistration", State, principalId);
        if (!ReferenceEquals(next, State)) { await Commit(next, provision!.Member.BrainId); }
        return provision;
    }

    public async Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        await Check("RedeemExternalTicket", new IssuerIsKnown(issuer, auth.Value.TrustedIssuers));
        var normalizedEmail = DigitalBrain.Capabilities.Identity.ExternalSignInTicket.NormalizeEmail(email) ?? email.Trim();
        var principalId = External.ExternalSignIn.PrincipalOf(issuer, normalizedEmail);
        var name = string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim();
        return await CompleteRegistration(principalId) ?? await RegisterPrincipal(principalId, name);
    }

    public Task<BrainEntry[]> BrainsOf(string principalId) => Task.FromResult(PrincipalOf(State, principalId)?.Brains ?? []);

    public Task<BrainKind> KindOf(string brainScopeId) => Task.FromResult(BrainOf(State, brainScopeId)?.Kind ?? BrainKind.Personal);

    public Task<bool> BrainExists(string brainScopeId) => Task.FromResult(BrainOf(State, brainScopeId)?.AccountId is not null);

    public Task<Member?> Membership(string brainScopeId, string principalId)
        => Task.FromResult(MemberOf(BrainOf(State, brainScopeId), principalId));

    public async Task<Member?> EnsureMember(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var next = await AddMember(State, member);
        await Commit(next, member.BrainId);
        return MemberOf(BrainOf(next, member.BrainId), member.PrincipalId);
    }

    public async Task RemoveMember(string brainScopeId, string accountId)
    {
        var brain = BrainOf(State, brainScopeId);
        await Check("RemoveMember",
            new BrainIsProvisioned(brainScopeId, brain),
            new MemberIsNotBrainOwner(brainScopeId, accountId, brain, null));
        var next = State;
        foreach (var principal in brain!.Members.Where(item => item.AccountId == accountId).Select(item => item.PrincipalId).ToArray())
        { next = DropMember(next, brainScopeId, principal); }
        await Commit(next, brainScopeId);
    }

    public async Task RemovePrincipalMember(string brainScopeId, string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var brain = BrainOf(State, brainScopeId);
        if (brain is null || MemberOf(brain, principalId) is not { } member) { return; }
        await Check("RemovePrincipalMember", new MemberIsNotBrainOwner(brainScopeId, principalId, brain, member));
        await Commit(DropMember(State, brainScopeId, principalId), brainScopeId);
    }

    // The authority reads and writes a brain's grants as the platform, as EdgeGrantsCache does. Under the caller being decided,
    // the grants neuron's incoming filter would ask this directory again while it waits on the grants neuron: a call cycle that
    // only the 30 s timeout ended, so every first call after a start failed (reconciler, compiled apps, brain scene).
    private static CapabilityCaller AuthorityReader(string brain) => CapabilityCallers.Platform(IDirectoryStore.Type, "system", brain);

    private Grants.IGrantsNeuron? GrantsOf(string brain)
        => services?.GetService<IGrainFactory>() is not null
            ? SiloNeurons.Get<Grants.IGrantsNeuron>(services, Grants.IGrantsNeuron.Key(brain))
            : null;

    private async Task<Grant[]> ReadGrants(string brain)
    {
        if (GrantsOf(brain) is not { } grants) { return []; }
        using var asAuthority = RuntimeCallerContext.Stamp(AuthorityReader(brain));
        return await grants.List();
    }

    private async Task WriteGrant(string brain, Grant grant)
    {
        if (GrantsOf(brain) is not { } grants) { return; }
        using var asAuthority = RuntimeCallerContext.Stamp(AuthorityReader(brain));
        await grants.Grant(grant);
    }

    private async Task RevokeGrant(string brain, string grantId)
    {
        if (GrantsOf(brain) is not { } grants) { return; }
        using var asAuthority = RuntimeCallerContext.Stamp(AuthorityReader(brain));
        await grants.Revoke(grantId);
    }

    public Task<CallDecision?> Authorize(DirectoryAuthorizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ToIdentity() is not { } caller)
        {
            return Task.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller."));
        }
        return AuthorizeIdentity(caller, request.TargetNeuron, request.Operation, request.SemanticTypeIds ?? [], request.TargetScope);
    }

    internal async Task<CallDecision?> AuthorizeIdentity(
        CallerIdentity caller,
        string targetNeuron,
        string operation,
        IReadOnlyList<string> semanticTypeIds,
        string? targetScope)
    {
        var brainId = caller.BrainId;
        var brain = BrainOf(State, brainId) ?? new BrainRow { BrainId = brainId };
        var grantsBrain = BrainAuthorityDecisions.GrantsBrainOf(brainId, targetNeuron, targetScope);
        var durableGrants = await ReadGrants(grantsBrain);
        var allGrants = durableGrants.Length > 0 ? durableGrants : brain.Grants;
        if (caller.Kind == CallerKind.Agent && !allGrants.Any(g => g.AppId == CapabilityCallers.AssistantApp))
        {
            allGrants = [.. allGrants, .. ShippedGrants.ForAssistant(grantsBrain)];
        }
        var decision = await BrainAuthorityDecisions.Decide(
            brain with { Grants = allGrants },
            caller,
            targetNeuron,
            operation,
            semanticTypeIds,
            targetScope,
            services?.GetService<IEdgeGrantsCache>());
        if (decision is { Allowed: true })
        {
            var consumed = GrantRules.OnceToConsume(caller, targetNeuron, semanticTypeIds, allGrants);
            if (consumed.Count > 0)
            {
                decision = decision with { ConsumesOnce = true };
                foreach (var c in consumed)
                {
                    await RevokeGrant(grantsBrain, c.Id);
                }
                var grants = brain.Grants.Where(grant => !consumed.Contains(grant)).ToArray();
                await Commit(ReplaceBrain(State, brain with { Grants = grants }), brain.BrainId);
            }
        }
        return decision;
    }

    public async Task<Grant[]> ListGrants(string brainScopeId)
    {
        var list = await ReadGrants(brainScopeId);
        if (list.Length > 0) { return list; }
        return BrainOf(State, brainScopeId)?.Grants.Where(grant => !grant.Revoked).ToArray() ?? [];
    }

    public async Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant)
    {
        var next = RequireOwner(State, caller);
        await Check("GrantAsOwner", new BrainAuthorityIsInitialized(BrainOf(next, caller.BrainId)));
        var normalized = Grant.Normalize(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized.Grantee);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized.Scope);
        if (normalized.Target.Length > 0 && !InBrain(normalized.Target, caller.BrainId))
        { throw new UnauthorizedAccessException("A brain owner grants access only to neurons in that brain."); }
        var stored = normalized with
        {
            Owner = string.IsNullOrEmpty(normalized.Owner) ? $"brain:{caller.BrainId}" : normalized.Owner,
            WorkspaceId = caller.BrainId,
            GrantedAt = clock.GetUtcNow(),
        };
        var brain = BrainOf(next, caller.BrainId)!;
        var grants = brain.Grants.Where(item =>
        {
            var n = Grant.Normalize(item);
            return n.Id != stored.Id && !(string.Equals(n.Grantee, stored.Grantee, StringComparison.Ordinal)
                && string.Equals(n.Target, stored.Target, StringComparison.Ordinal)
                && string.Equals(n.Scope, stored.Scope, StringComparison.Ordinal));
        }).Append(stored).ToArray();
        await Commit(ReplaceBrain(next, brain with { Grants = grants }), caller.BrainId);

        await WriteGrant(caller.BrainId, stored);
        return stored;
    }

    public async Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentNullException.ThrowIfNull(grants);
        if (BrainOf(State, brainScopeId) is not { } brain) { return []; }
        List<Grant> added = [];
        foreach (var grant in grants)
        {
            var normalized = Grant.Normalize(grant);
            var assistantKey = IntoChat.OS.Primitives.AppRef.Of(new BrainId(brainScopeId), CapabilityCallers.AssistantApp).Key;
            var isAssistantGrantee = string.Equals(normalized.Grantee, $"app:{CapabilityCallers.AssistantApp}", StringComparison.Ordinal)
                || string.Equals(normalized.Grantee, $"app:{assistantKey}", StringComparison.Ordinal);
            var isAppGrantee = normalized.Grantee.StartsWith("app:", StringComparison.Ordinal)
                && IntoChat.OS.Primitives.AppRef.TryParse(new BrainId(brainScopeId), normalized.Grantee["app:".Length..], out _);
            if ((!isAppGrantee && !isAssistantGrantee) || (!string.Equals(normalized.Target, "*", StringComparison.Ordinal) && !InBrain(normalized.Target, brainScopeId)))
            { throw new UnauthorizedAccessException("A shipped app is granted only neurons of the brain it is installed in."); }
            if (brain.Grants.Concat(added).Select(Grant.Normalize).Any(held => !held.Revoked
                && string.Equals(held.Grantee, normalized.Grantee, StringComparison.Ordinal)
                && string.Equals(held.Target, normalized.Target, StringComparison.Ordinal)
                && string.Equals(held.Scope, normalized.Scope, StringComparison.Ordinal))) { continue; }
            added.Add(normalized with { Owner = $"brain:{brainScopeId}", WorkspaceId = brainScopeId, GrantedAt = clock.GetUtcNow() });
        }
        if (added.Count == 0) { return []; }
        await Commit(ReplaceBrain(State, brain with { Grants = [.. brain.Grants, .. added] }), brainScopeId);
        foreach (var grant in added) { await WriteGrant(brainScopeId, grant); }
        return [.. added];
    }

    // A target names its brain first ({brain}/...) or, for a plain neuron, after its type ({type}/{brain}/...).
    internal static bool InBrain(string target, string brainId)
    {
        if (string.Equals(target, "*", StringComparison.Ordinal)) { return true; }
        if (EdgeGrantsCache.TryExtractBrain(target, out var extracted) && string.Equals(extracted.Value, brainId, StringComparison.Ordinal)) { return true; }
        var slash = target.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && EdgeGrantsCache.TryExtractBrain(target[(slash + 1)..], out extracted) && string.Equals(extracted.Value, brainId, StringComparison.Ordinal);
    }

    public async Task RevokeAsOwner(CapabilityCaller caller, string grantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        var next = RequireOwner(State, caller);
        var brain = BrainOf(next, caller.BrainId)!;
        var grants = brain.Grants.Where(item => item.Id != grantId).ToArray();
        await Commit(ReplaceBrain(next, brain with { Grants = grants }), caller.BrainId);

        await RevokeGrant(caller.BrainId, grantId);
    }

    [Obsolete("Use RevokeAsOwner with grantId")]
    public async Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode)
    {
        var next = RequireOwner(State, caller);
        var brain = BrainOf(next, caller.BrainId)!;
        var grants = brain.Grants.Where(item => item.AppId != appId || item.SemanticTypeId != semanticTypeId || item.Mode != mode).ToArray();
        await Commit(ReplaceBrain(next, brain with { Grants = grants }), caller.BrainId);

        var activeGrants = await ReadGrants(caller.BrainId);
        foreach (var item in activeGrants.Where(item => item.AppId == appId && item.SemanticTypeId == semanticTypeId && item.Mode == mode))
        {
            await RevokeGrant(caller.BrainId, item.Id);
        }
    }

    private Task<(DirectoryState Next, bool Created)> Provision(string operation, DirectoryState state, Member owner)
    {
        var account = AccountOf(state, owner.AccountId) ?? throw new UnauthorizedAccessException("Brain ownership does not match the account.");
        if (account.OwnerPrincipalId != owner.PrincipalId) { throw new UnauthorizedAccessException("Brain ownership does not match the account."); }
        var next = Initialize(state, owner.AccountId, owner.BrainId);
        return ProvisionOwnerRow(operation, next, owner);
    }

    private async Task<(DirectoryState Next, bool Created)> ProvisionOwnerRow(string operation, DirectoryState state, Member owner)
    {
        await Check(operation, new MemberIsOwnerRole(owner));
        var brain = BrainOf(state, owner.BrainId)!;
        if (brain.OwnerProvisioned)
        {
            var current = brain.Members.FirstOrDefault(member => member.Role == MemberRole.Owner);
            if (current is null || current.PrincipalId == owner.PrincipalId) { return (state, false); }
            await Check(operation, new BrainOwnerMatches(owner.BrainId, current.PrincipalId, owner.PrincipalId));
        }
        var members = brain.Members.Where(member => member.PrincipalId != owner.PrincipalId).Append(owner).ToArray();
        var next = ReplaceBrain(state, brain with { Members = members, OwnerProvisioned = true });
        return (JoinBrain(next, owner.PrincipalId, owner.BrainId, brain.Kind), true);
    }

    private async Task<(DirectoryState Next, BrainProvision? Provision)> FinishRegistration(string operation, DirectoryState state, string principalId)
    {
        var row = PrincipalOf(state, principalId);
        if (row?.DefaultMembership is not { } member) { return (state, null); }
        if (row.RegistrationComplete) { return (state, new BrainProvision(member, false)); }
        var account = new Account { AccountId = member.AccountId, OwnerPrincipalId = member.PrincipalId, Name = member.DisplayName, CreatedAt = member.JoinedAt };
        var withAccount = AccountOf(state, account.AccountId) is null ? state with { Accounts = [.. state.Accounts, account] } : state;
        var (provisioned, created) = await Provision(operation, withAccount, member);
        var completed = UpsertPrincipal(provisioned, PrincipalOf(provisioned, principalId)! with { RegistrationComplete = true });
        return (completed, new BrainProvision(member, created));
    }

    private async Task<DirectoryState> AddMember(DirectoryState state, Member member)
    {
        var brain = BrainOf(state, member.BrainId);
        if (brain?.AccountId is { } owner && owner != member.AccountId)
        {
            await Check("EnsureMember", new BrainHasSingleOwner(member));
        }
        else { state = Initialize(state, member.AccountId, member.BrainId); brain = BrainOf(state, member.BrainId); }
        if (brain!.Members.FirstOrDefault(item => item.PrincipalId == member.PrincipalId) is { } existing)
        {
            await Check("EnsureMember", new MemberRoleMatches(existing.Role, member.Role));
            return state;
        }
        var members = brain.Members.Append(member).ToArray();
        return JoinBrain(ReplaceBrain(state, brain with { Members = members }), member.PrincipalId, member.BrainId, brain.Kind);
    }

    private static DirectoryState DropMember(DirectoryState state, string brainId, string principalId)
    {
        var brain = BrainOf(state, brainId)!;
        var members = brain.Members.Where(member => member.PrincipalId != principalId).ToArray();
        return LeaveBrain(ReplaceBrain(state, brain with { Members = members }), principalId, brainId);
    }

    private DirectoryState RequireOwner(DirectoryState state, CapabilityCaller caller)
    {
        if (!RuntimeCallerContext.IsTrusted(caller) || caller.Kind != CallerKind.User || caller.BrainId.Length == 0)
        { throw new UnauthorizedAccessException("Only the current brain owner can manage grants."); }
        if (MemberOf(BrainOf(state, caller.BrainId), caller.PrincipalId)?.Role != MemberRole.Owner)
        { throw new UnauthorizedAccessException("Only the current brain owner can manage grants."); }
        return state;
    }

    private static DirectoryState Initialize(DirectoryState state, string accountId, string brainId)
    {
        var brain = BrainOf(state, brainId);
        if (brain is null) { return ReplaceBrain(state, new BrainRow { BrainId = brainId, AccountId = accountId, Grants = ShippedGrants.ForAssistant(brainId) }); }
        if (brain.AccountId is not null && (brain.AccountId != accountId || brain.BrainId != brainId))
        {
            // authority: brain authority scope conflict is an authority invariant
            throw new InvalidOperationException("Brain authority scope conflict.");
        }
        return brain.AccountId is null ? ReplaceBrain(state, brain with { AccountId = accountId }) : state;
    }

    private async Task<DirectoryState> Classify(string operation, DirectoryState state, string brainId, BrainKind kind)
    {
        var brain = BrainOf(state, brainId) ?? new BrainRow { BrainId = brainId };
        if (brain.Kind == kind) { return BrainOf(state, brainId) is null ? ReplaceBrain(state, brain) : state; }
        await Check(operation, new BrainKindIsMutable(brain));
        return ReplaceBrain(state, brain with { Kind = kind });
    }

    private static DirectoryState JoinBrain(DirectoryState state, string principalId, string brainId, BrainKind kind)
    {
        var row = PrincipalOf(state, principalId) ?? new PrincipalRow { PrincipalId = principalId };
        if (row.Brains.Any(known => known.Id == brainId)) { return PrincipalOf(state, principalId) is null ? UpsertPrincipal(state, row) : state; }
        return UpsertPrincipal(state, row with { Brains = [.. row.Brains, new BrainEntry(brainId, kind)] });
    }

    private static DirectoryState LeaveBrain(DirectoryState state, string principalId, string brainId)
    {
        var row = PrincipalOf(state, principalId);
        if (row is null || !row.Brains.Any(known => known.Id == brainId)) { return state; }
        return UpsertPrincipal(state, row with { Brains = [.. row.Brains.Where(known => known.Id != brainId)] });
    }

    private async Task Commit(DirectoryState next, string scope)
    {
        if (ReferenceEquals(next, State)) { return; }
        await Save(next, new DirectoryChanged(scope));
    }

    private void PublishMirror()
    {
        foreach (var brain in State.Brains)
        {
            if (brain.BrainId.Length > 0) { mirror.Publish(brain.BrainId, brain); }
        }
    }

    private static Account? AccountOf(DirectoryState state, string accountId) => state.Accounts.FirstOrDefault(account => account.AccountId == accountId);

    private static PrincipalRow? PrincipalOf(DirectoryState state, string principalId)
        => state.Principals.FirstOrDefault(row => row.PrincipalId == principalId);

    private static BrainRow? BrainOf(DirectoryState state, string brainId)
        => state.Brains.FirstOrDefault(row => row.BrainId == brainId);

    private static Member? OperationOf(DirectoryState state, string accountId, string operationId)
        => state.Operations.FirstOrDefault(row => row.AccountId == accountId && row.OperationId == operationId)?.Member;

    private static Member? MemberOf(BrainRow? brain, string principalId)
        => brain?.Members.FirstOrDefault(member => member.PrincipalId == principalId);

    private static DirectoryState UpsertPrincipal(DirectoryState state, PrincipalRow row)
        => state with { Principals = [.. state.Principals.Where(item => item.PrincipalId != row.PrincipalId), row] };

    private static DirectoryState ReplaceBrain(DirectoryState state, BrainRow row)
        => state with { Brains = [.. state.Brains.Where(item => item.BrainId != row.BrainId), row] };
}
