using DigitalBrain.Capabilities;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
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
    Task<CallDecision?> Authorize(CallRequest request);
    Task<Grant[]> ListGrants(string brainScopeId);
    Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant);
    Task RevokeAsOwner(CapabilityCaller caller, string grantId);
    Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode);
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

[DigitalBrain.Neuron(IDirectoryStore.Type)]
internal sealed class DirectoryNeuron(TimeProvider clock, IOptions<AuthOptions> auth, AuthorityMirror mirror) : DigitalBrain.Neuron<DirectoryState>, IDirectoryStore
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
            if (existing.OwnerPrincipalId != account.OwnerPrincipalId) { throw new InvalidOperationException("Account ownership conflict."); }
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
        next = Classify(next, member.BrainId, kind);
        var (provisioned, created) = Provision(next, member);
        await Commit(provisioned, member.BrainId);
        return new BrainProvision(member, created);
    }

    public async Task<BrainProvision> EnsureBrain(Member owner)
    {
        var (next, created) = Provision(State, owner);
        await Commit(next, owner.BrainId);
        return new BrainProvision(owner, created);
    }

    public async Task<BrainProvision> ProvisionOwner(Member owner)
    {
        var (next, created) = Provision(State, owner);
        await Commit(next, owner.BrainId);
        return new BrainProvision(owner, created);
    }

    public Task<bool> IsRegistered(string principalId) => Task.FromResult(PrincipalOf(State, principalId)?.RegistrationComplete == true);

    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName)
    {
        if (principalId.Length is 0 or > 80 || !System.Text.RegularExpressions.Regex.IsMatch(principalId, "^[a-z0-9][a-z0-9-]*$"))
        { throw new ArgumentException("Use a lowercase username containing letters, numbers and hyphens."); }
        if (principalId is "owner" or "integrations" || PrincipalOf(State, principalId)?.DefaultMembership is not null)
        { throw new InvalidOperationException("That username is unavailable."); }
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
        var (completed, provision) = FinishRegistration(next, principalId);
        await Commit(completed, member.BrainId);
        return provision!;
    }

    public async Task<BrainProvision?> CompleteRegistration(string principalId)
    {
        if (PrincipalOf(State, principalId)?.DefaultMembership is null) { return null; }
        var (next, provision) = FinishRegistration(State, principalId);
        if (!ReferenceEquals(next, State)) { await Commit(next, provision!.Member.BrainId); }
        return provision;
    }

    public Task<BrainEntry[]> BrainsOf(string principalId) => Task.FromResult(PrincipalOf(State, principalId)?.Brains ?? []);

    public Task<BrainKind> KindOf(string brainScopeId) => Task.FromResult(BrainOf(State, brainScopeId)?.Kind ?? BrainKind.Personal);

    public Task<bool> BrainExists(string brainScopeId) => Task.FromResult(BrainOf(State, brainScopeId)?.AccountId is not null);

    public Task<Member?> Membership(string brainScopeId, string principalId)
        => Task.FromResult(MemberOf(BrainOf(State, brainScopeId), principalId));

    public async Task<Member?> EnsureMember(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var next = AddMember(State, member);
        await Commit(next, member.BrainId);
        return MemberOf(BrainOf(next, member.BrainId), member.PrincipalId);
    }

    public async Task RemoveMember(string brainScopeId, string accountId)
    {
        var brain = BrainOf(State, brainScopeId) ?? throw new InvalidOperationException($"Brain {brainScopeId} is not provisioned.");
        if (accountId == brain.AccountId) { throw new InvalidOperationException($"The owner of brain {brainScopeId} cannot be removed from it."); }
        var next = State;
        foreach (var principal in brain.Members.Where(item => item.AccountId == accountId).Select(item => item.PrincipalId).ToArray())
        { next = DropMember(next, brainScopeId, principal); }
        await Commit(next, brainScopeId);
    }

    public async Task RemovePrincipalMember(string brainScopeId, string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var brain = BrainOf(State, brainScopeId);
        if (brain is null || MemberOf(brain, principalId) is not { } member) { return; }
        if (member.Role == MemberRole.Owner || principalId == brain.AccountId)
        { throw new InvalidOperationException($"The owner of brain {brainScopeId} cannot be removed from it."); }
        await Commit(DropMember(State, brainScopeId, principalId), brainScopeId);
    }

    public async Task<CallDecision?> Authorize(CallRequest request)
    {
        var brain = BrainOf(State, request.Caller.BrainId) ?? new BrainRow { BrainId = request.Caller.BrainId };
        var grantsNeuron = SiloNeurons.Get<Grants.IGrantsNeuron>(Context.Services, Grants.IGrantsNeuron.Key(request.Caller.BrainId));
        var durableGrants = await grantsNeuron.List();
        var allGrants = durableGrants.Length > 0 ? durableGrants : brain.Grants;
        var decision = BrainAuthorityDecisions.Decide(brain with { Grants = allGrants }, request);
        if (decision is { Allowed: true })
        {
            var consumed = GrantRules.OnceToConsume(request, allGrants);
            if (consumed.Count > 0)
            {
                decision = decision with { ConsumesOnce = true };
                foreach (var c in consumed)
                {
                    await grantsNeuron.Revoke(c.Id);
                }
                var grants = brain.Grants.Where(grant => !consumed.Contains(grant)).ToArray();
                await Commit(ReplaceBrain(State, brain with { Grants = grants }), brain.BrainId);
            }
        }
        return decision;
    }

    public async Task<Grant[]> ListGrants(string brainScopeId)
    {
        var grantsNeuron = SiloNeurons.Get<Grants.IGrantsNeuron>(Context.Services, Grants.IGrantsNeuron.Key(brainScopeId));
        var list = await grantsNeuron.List();
        if (list.Length > 0) { return list; }
        return BrainOf(State, brainScopeId)?.Grants.Where(grant => !grant.Revoked).ToArray() ?? [];
    }

    public async Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant)
    {
        var next = RequireOwner(State, caller);
        if (BrainOf(next, caller.BrainId) is null) { throw new InvalidOperationException("Initialize the brain authority before granting access."); }
        var normalized = Grant.Normalize(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized.Grantee);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized.Scope);
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

        var grantsNeuron = SiloNeurons.Get<Grants.IGrantsNeuron>(Context.Services, Grants.IGrantsNeuron.Key(caller.BrainId));
        await grantsNeuron.Grant(stored);
        return stored;
    }

    public async Task RevokeAsOwner(CapabilityCaller caller, string grantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        var next = RequireOwner(State, caller);
        var brain = BrainOf(next, caller.BrainId)!;
        var grants = brain.Grants.Where(item => item.Id != grantId).ToArray();
        await Commit(ReplaceBrain(next, brain with { Grants = grants }), caller.BrainId);

        var grantsNeuron = SiloNeurons.Get<Grants.IGrantsNeuron>(Context.Services, Grants.IGrantsNeuron.Key(caller.BrainId));
        await grantsNeuron.Revoke(grantId);
    }

    [Obsolete("Use RevokeAsOwner with grantId")]
    public async Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode)
    {
        var next = RequireOwner(State, caller);
        var brain = BrainOf(next, caller.BrainId)!;
        var grants = brain.Grants.Where(item => item.AppId != appId || item.SemanticTypeId != semanticTypeId || item.Mode != mode).ToArray();
        await Commit(ReplaceBrain(next, brain with { Grants = grants }), caller.BrainId);

        var grantsNeuron = SiloNeurons.Get<Grants.IGrantsNeuron>(Context.Services, Grants.IGrantsNeuron.Key(caller.BrainId));
        var activeGrants = await grantsNeuron.List();
        foreach (var item in activeGrants.Where(item => item.AppId == appId && item.SemanticTypeId == semanticTypeId && item.Mode == mode))
        {
            await grantsNeuron.Revoke(item.Id);
        }
    }

    private (DirectoryState Next, bool Created) Provision(DirectoryState state, Member owner)
    {
        var account = AccountOf(state, owner.AccountId) ?? throw new UnauthorizedAccessException("Brain ownership does not match the account.");
        if (account.OwnerPrincipalId != owner.PrincipalId) { throw new UnauthorizedAccessException("Brain ownership does not match the account."); }
        var next = Initialize(state, owner.AccountId, owner.BrainId);
        return ProvisionOwnerRow(next, owner);
    }

    private (DirectoryState Next, bool Created) ProvisionOwnerRow(DirectoryState state, Member owner)
    {
        if (owner.Role != MemberRole.Owner) { throw new ArgumentException("An owner is required."); }
        var brain = BrainOf(state, owner.BrainId)!;
        if (brain.OwnerProvisioned)
        {
            var current = brain.Members.FirstOrDefault(member => member.Role == MemberRole.Owner);
            if (current is null || current.PrincipalId == owner.PrincipalId) { return (state, false); }
            throw new InvalidOperationException($"Brain {owner.BrainId} already belongs to {current.PrincipalId}; {owner.PrincipalId} cannot be provisioned as its owner.");
        }
        var members = brain.Members.Where(member => member.PrincipalId != owner.PrincipalId).Append(owner).ToArray();
        var next = ReplaceBrain(state, brain with { Members = members, OwnerProvisioned = true });
        return (JoinBrain(next, owner.PrincipalId, owner.BrainId, brain.Kind), true);
    }

    private (DirectoryState Next, BrainProvision? Provision) FinishRegistration(DirectoryState state, string principalId)
    {
        var row = PrincipalOf(state, principalId);
        if (row?.DefaultMembership is not { } member) { return (state, null); }
        if (row.RegistrationComplete) { return (state, new BrainProvision(member, false)); }
        var account = new Account { AccountId = member.AccountId, OwnerPrincipalId = member.PrincipalId, Name = member.DisplayName, CreatedAt = member.JoinedAt };
        var withAccount = AccountOf(state, account.AccountId) is null ? state with { Accounts = [.. state.Accounts, account] } : state;
        var (provisioned, created) = Provision(withAccount, member);
        var completed = UpsertPrincipal(provisioned, PrincipalOf(provisioned, principalId)! with { RegistrationComplete = true });
        return (completed, new BrainProvision(member, created));
    }

    private DirectoryState AddMember(DirectoryState state, Member member)
    {
        var brain = BrainOf(state, member.BrainId);
        if (brain?.AccountId is { } owner && owner != member.AccountId)
        {
            if (member.Role == MemberRole.Owner) { throw new InvalidOperationException("A brain has one owner, its creator."); }
        }
        else { state = Initialize(state, member.AccountId, member.BrainId); brain = BrainOf(state, member.BrainId); }
        if (brain!.Members.FirstOrDefault(item => item.PrincipalId == member.PrincipalId) is { } existing)
        {
            if (existing.Role != member.Role) { throw new InvalidOperationException("Membership role conflict."); }
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
        if (!CallerContextStamper.IsTrusted(caller) || caller.Kind != CallerKind.User || caller.BrainId.Length == 0)
        { throw new UnauthorizedAccessException("Only the current brain owner can manage grants."); }
        if (AuthOptions.IsOpenPostureOwner(auth.Value.Posture, caller)) { return Initialize(state, caller.AccountId, caller.BrainId); }
        if (MemberOf(BrainOf(state, caller.BrainId), caller.PrincipalId)?.Role != MemberRole.Owner)
        { throw new UnauthorizedAccessException("Only the current brain owner can manage grants."); }
        return state;
    }

    private static DirectoryState Initialize(DirectoryState state, string accountId, string brainId)
    {
        var brain = BrainOf(state, brainId);
        if (brain is null) { return ReplaceBrain(state, new BrainRow { BrainId = brainId, AccountId = accountId }); }
        if (brain.AccountId is not null && (brain.AccountId != accountId || brain.BrainId != brainId))
        { throw new InvalidOperationException("Brain authority scope conflict."); }
        return brain.AccountId is null ? ReplaceBrain(state, brain with { AccountId = accountId }) : state;
    }

    private static DirectoryState Classify(DirectoryState state, string brainId, BrainKind kind)
    {
        var brain = BrainOf(state, brainId) ?? new BrainRow { BrainId = brainId };
        if (brain.Kind == kind) { return BrainOf(state, brainId) is null ? ReplaceBrain(state, brain) : state; }
        if (brain.OwnerProvisioned) { throw new InvalidOperationException($"Brain {brainId} is a {brain.Kind} brain; its kind is fixed."); }
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
