namespace DigitalBrain.Identity.Local.Directory;

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Identity.Model;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

/// <summary>Durable local directory for accounts, principals, brains, memberships, and grants.</summary>
[NeuronAlias(IDirectory.Type), DefaultNeuronType(IDirectory.Type), Shareable]
public interface IDirectory
{
    /// <summary>The frozen neuron type name.</summary>
    public const string Type = "identity.directory";

    /// <summary>The single directory neuron key in the system brain.</summary>
    public const string Key = "system/main";

    /// <summary>Reads an account by identifier, or null when missing.</summary>
    Task<Account?> ReadAccount(string accountId);

    /// <summary>Reads the owning account of a brain, or null when missing.</summary>
    Task<Account?> OwnerOfBrain(string brainId);

    /// <summary>Ensures an account exists (idempotent per account identifier).</summary>
    Task EnsureAccount(Account account);

    /// <summary>Creates a brain idempotently per operation identifier.</summary>
    Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind);

    /// <summary>Ensures a brain exists for <paramref name="owner"/>.</summary>
    Task<BrainProvision> EnsureBrain(Member owner);

    /// <summary>Provisions the account and brain for <paramref name="owner"/>.</summary>
    Task<BrainProvision> ProvisionOwner(Member owner);

    /// <summary>Checks whether <paramref name="principalId"/> is registered.</summary>
    Task<bool> IsRegistered(string principalId);

    /// <summary>Registers <paramref name="principalId"/> idempotently.</summary>
    Task<BrainProvision> RegisterPrincipal(string principalId, string displayName);

    /// <summary>Completes registration for <paramref name="principalId"/>, or null when unknown.</summary>
    Task<BrainProvision?> CompleteRegistration(string principalId);

    /// <summary>Lists brains that <paramref name="principalId"/> belongs to.</summary>
    Task<BrainEntry[]> BrainsOf(string principalId);

    /// <summary>Returns the kind of <paramref name="brainScopeId"/> (<see cref="BrainKind.Personal"/> when unknown).</summary>
    Task<BrainKind> KindOf(string brainScopeId);

    /// <summary>Checks whether <paramref name="brainScopeId"/> exists.</summary>
    Task<bool> BrainExists(string brainScopeId);

    /// <summary>Returns the membership of <paramref name="principalId"/> in <paramref name="brainScopeId"/>, or null.</summary>
    Task<Member?> Membership(string brainScopeId, string principalId);

    // The six members below decide by the stamped caller, so they serve a caller of any brain (Shared); the others are the
    // platform's and are reached only as the kernel.

    /// <summary>Ensures <paramref name="member"/> belongs to its brain when called by the brain's owner.</summary>
    [DigitalBrain.Access.Shared]
    Task<Member?> EnsureMember(Member member);

    /// <summary>Removes all non-owner memberships for <paramref name="accountId"/> in <paramref name="brainScopeId"/>.</summary>
    [DigitalBrain.Access.Shared]
    Task RemoveMember(string brainScopeId, string accountId);

    /// <summary>Removes a non-owner principal from <paramref name="brainScopeId"/>.</summary>
    [DigitalBrain.Access.Shared]
    Task RemovePrincipalMember(string brainScopeId, string principalId);

    /// <summary>Authorizes <paramref name="request"/> for the stamped caller in the brain it acts in.</summary>
    [DigitalBrain.Access.Shared]
    Task<CallDecision?> Authorize(CallRequest request);

    /// <summary>Lists active grants for <paramref name="brainScopeId"/>.</summary>
    Task<Grant[]> ListGrants(string brainScopeId);

    /// <summary>Records <paramref name="grant"/> in <paramref name="brainId"/> when the stamped caller is its owner in person, or null when not.</summary>
    [DigitalBrain.Access.Shared]
    Task<Grant?> GrantAsOwner(string brainId, Grant grant);

    /// <summary>Revokes matching grants in <paramref name="brainId"/> when the stamped caller is its owner in person.</summary>
    [DigitalBrain.Access.Shared]
    Task<bool> RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode);

    /// <summary>Revokes a specific grant by identifier in <paramref name="brainId"/> when the stamped caller is its owner in person.</summary>
    [DigitalBrain.Access.Shared]
    Task<bool> RevokeGrantAsOwner(string brainId, string grantId);

    /// <summary>Idempotently records platform-shipped grants for <paramref name="brainScopeId"/>; returns newly added grants.</summary>
    Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants);

    /// <summary>Returns a point-in-time snapshot of <paramref name="brainId"/> for policy evaluation.</summary>
    Task<DirectoryBrainSnapshot> SnapshotOf(string brainId);

    /// <summary>Atomically spends one-time grants by identifier in <paramref name="brainId"/>; returns false when any is no longer live.</summary>
    Task<bool> SpendOnce(string brainId, string[] grantIds);

    /// <summary>Links <paramref name="linkedPrincipalId"/> to <paramref name="principalId"/>'s default membership and brains.</summary>
    Task<PrincipalLinkStatus> LinkPrincipal(string principalId, string linkedPrincipalId);

    /// <summary>Promotes an ephemeral brain into the durable directory under <paramref name="principalId"/>, preserving <paramref name="brainScopeId"/>.</summary>
    Task<BrainProvision> ClaimBrain(string brainScopeId, string principalId, string displayName, Grant[]? carriedGrants = null);
}

/// <summary>Emitted when directory state changes for a scope. Kept for historical journal compatibility.</summary>
[Signal("identity.directory-changed")]
public sealed record DirectoryChanged(string Scope) : Signal;

/// <summary>A brain record inside <see cref="DirectoryState"/>.</summary>
public sealed record BrainRow
{
    /// <summary>The brain identifier.</summary>
    public string BrainId { get; init; } = "";

    /// <summary>The owning account identifier.</summary>
    public string AccountId { get; init; } = "";

    /// <summary>The brain kind.</summary>
    public BrainKind Kind { get; init; }

    /// <summary>Legacy nested members migrated into <see cref="DirectoryState.Members"/> on activation and cleared.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Member[]? Members { get; init; }

    /// <summary>Legacy nested grants migrated into <see cref="DirectoryState.Grants"/> on activation and cleared.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Grant[]? Grants { get; init; }

    /// <summary>Legacy owner-provisioned marker cleared after migration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OwnerProvisioned { get; init; }
}

/// <summary>Persisted state of <see cref="DirectoryNeuron"/>.</summary>
public sealed record DirectoryState
{
    /// <summary>All created accounts.</summary>
    public Account[] Accounts { get; init; } = [];

    /// <summary>Registered principals.</summary>
    public PrincipalRecord[] Principals { get; init; } = [];

    /// <summary>Provisioned brains.</summary>
    public BrainRow[] Brains { get; init; } = [];

    /// <summary>Brain memberships.</summary>
    public Member[] Members { get; init; } = [];

    /// <summary>Capability grants.</summary>
    public Grant[] Grants { get; init; } = [];

    /// <summary>Completed brain creation operations.</summary>
    public BrainCreation[] Operations { get; init; } = [];

    internal Account? AccountOf(string accountId)
        => Accounts.FirstOrDefault(a => a.AccountId == accountId);

    internal PrincipalRecord? PrincipalOf(string principalId)
        => Principals.FirstOrDefault(p => p.PrincipalId == principalId);

    internal BrainRow? BrainOf(string brainId)
        => Brains.FirstOrDefault(b => b.BrainId == brainId);

    internal Member? OwnerMemberOf(string brainId)
        => Members.FirstOrDefault(m => m.BrainId == brainId && m.Role == MemberRole.Owner);

    internal Member? MemberOf(string brainScopeId, string principalId)
        => Members.FirstOrDefault(m => m.BrainId == brainScopeId && m.PrincipalId == principalId);

    internal Account? OwnerAccountOf(string brainId)
    {
        var accountId = BrainOf(brainId)?.AccountId ?? OwnerMemberOf(brainId)?.AccountId;
        return accountId is null ? null : AccountOf(accountId);
    }

    internal BrainDirectory Brain(string brainId)
    {
        var brain = BrainOf(brainId);
        var accountId = brain?.AccountId ?? OwnerMemberOf(brainId)?.AccountId ?? "";
        var kind = brain?.Kind ?? BrainKind.Personal;
        var members = Members.Where(m => m.BrainId == brainId).ToImmutableArray();
        var grants = Grants.Where(g => g.WorkspaceId == brainId).ToImmutableArray();
        return new BrainDirectory
        {
            BrainId = brainId,
            AccountId = accountId,
            Kind = kind,
            Members = members,
            Grants = grants,
        };
    }

    internal DirectoryState With(BrainDirectory brain)
    {
        var otherMembers = Members.Where(m => m.BrainId != brain.BrainId);
        var otherGrants = Grants.Where(g => g.WorkspaceId != brain.BrainId);
        var brainExists = Brains.Any(b => b.BrainId == brain.BrainId);
        var brains = brainExists
            ? Brains
            : [.. Brains, new BrainRow { BrainId = brain.BrainId, AccountId = brain.AccountId, Kind = brain.Kind }];
        return this with
        {
            Brains = brains,
            Members = [.. otherMembers, .. brain.Members],
            Grants = [.. otherGrants, .. brain.Grants],
        };
    }

    internal AccountBook ToAccountBook() => new()
    {
        Accounts = [.. Accounts],
        Principals = [.. Principals],
        Operations = [.. Operations],
    };

    internal DirectoryState With(AccountBook book) => this with
    {
        Accounts = [.. book.Accounts],
        Principals = [.. book.Principals],
        Operations = [.. book.Operations],
    };

    internal BrainEntry[] BrainsOf(string principalId)
        => Members
            .Where(m => m.PrincipalId == principalId)
            .Select(m => m.BrainId)
            .Distinct(StringComparer.Ordinal)
            .Select(brainId => new BrainEntry(brainId, BrainOf(brainId)?.Kind ?? BrainKind.Personal))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Builds a <see cref="DirectoryBrainSnapshot"/> for <paramref name="brainId"/>.</summary>
    public DirectoryBrainSnapshot SnapshotOf(string brainId)
    {
        var brain = BrainOf(brainId);
        var members = Members.Where(m => m.BrainId == brainId).ToArray();
        var owner = members.FirstOrDefault(m => m.Role == MemberRole.Owner);
        var accountId = !string.IsNullOrEmpty(brain?.AccountId) ? brain.AccountId : owner?.AccountId;
        var grants = Grants.Where(g => g.WorkspaceId == brainId && !g.Revoked).ToArray();
        return new DirectoryBrainSnapshot
        {
            BrainId = brainId,
            AccountId = accountId,
            Members = members,
            Grants = grants,
            OwnerProvisioned = owner is not null,
            Kind = brain?.Kind ?? BrainKind.Personal,
        };
    }
}

/// <summary>Neuron implementing <see cref="IDirectory"/>, delegating domain logic to pure aggregates.</summary>
[Neuron(IDirectory.Type)]
public sealed class DirectoryNeuron(TimeProvider? clock = null, IDirectoryChangeObserver? observer = null) : Neuron<DirectoryState>, IDirectory
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IDirectoryChangeObserver? _observer = observer;

    /// <inheritdoc />
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (DirectoryStateMigration.TryMigrate(State, out var migrated))
        {
            await Save(migrated, new DirectoryChanged("migration")).ConfigureAwait(true);
        }

        _observer?.OnDirectoryChanged(State);
    }

    /// <inheritdoc />
    protected override void OnCommitted() => _observer?.OnDirectoryChanged(State);

    /// <inheritdoc />
    public Task<Account?> ReadAccount(string accountId)
        => Task.FromResult(State.AccountOf(accountId));

    /// <inheritdoc />
    public Task<Account?> OwnerOfBrain(string brainId)
        => Task.FromResult(State.OwnerAccountOf(brainId));

    /// <inheritdoc />
    public async Task EnsureAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (State.AccountOf(account.AccountId) is { } existing)
        {
            if (!string.IsNullOrEmpty(existing.OwnerPrincipalId)
                && !string.IsNullOrEmpty(account.OwnerPrincipalId)
                && !string.Equals(existing.OwnerPrincipalId, account.OwnerPrincipalId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Account ownership conflict.");
            }

            return;
        }

        var book = State.ToAccountBook();
        var (nextBook, _, facts) = book.EnsureAccount(account.AccountId, account.Name, account.OwnerPrincipalId, account.CreatedAt, _clock.GetUtcNow());
        if (facts.Length > 0)
        {
            await Save(State.With(nextBook), facts).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public async Task<BrainProvision> CreateBrain(
        string accountId,
        string principalId,
        string operationId,
        string displayName,
        BrainKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        if (State.AccountOf(accountId) is { } existingAccount
            && !string.IsNullOrEmpty(existingAccount.OwnerPrincipalId)
            && !string.Equals(existingAccount.OwnerPrincipalId, principalId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only the account owner can create a brain.");
        }

        var book = State.ToAccountBook();
        var (nextBook, provision, facts) = book.CreateBrain(accountId, principalId, operationId, displayName, kind, _clock.GetUtcNow());
        if (!provision.NewBrain)
        {
            return provision;
        }

        var next = State.With(nextBook);
        var brain = new BrainRow { BrainId = provision.Member.BrainId, AccountId = accountId, Kind = kind };
        next = next with
        {
            Brains = [.. next.Brains, brain],
            Members = [.. next.Members, provision.Member],
        };
        await Save(next, facts).ConfigureAwait(true);
        return provision;
    }

    /// <inheritdoc />
    public async Task<BrainProvision> EnsureBrain(Member owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (State.OwnerMemberOf(owner.BrainId) is { } existing)
        {
            return new BrainProvision(existing, false);
        }

        var now = _clock.GetUtcNow();
        var book = State.ToAccountBook();
        var (nextBook, _, accountFacts) = book.EnsureAccount(owner.AccountId, owner.DisplayName, owner.PrincipalId, owner.JoinedAt, now);

        var stamped = owner with
        {
            Role = MemberRole.Owner,
            JoinedAt = owner.JoinedAt == default ? now : owner.JoinedAt,
        };
        var next = State.With(nextBook);
        var brain = State.BrainOf(owner.BrainId) is null
            ? [.. next.Brains, new BrainRow { BrainId = owner.BrainId, AccountId = owner.AccountId, Kind = BrainKind.Personal }]
            : next.Brains;
        next = next with
        {
            Brains = brain,
            Members = [.. next.Members, stamped],
        };
        var facts = ImmutableArray.Create<Signal>(
            [.. accountFacts,
            new BrainCreated(owner.BrainId, owner.AccountId, BrainKind.Personal),
            new MemberAdded(stamped)]);
        await Save(next, facts).ConfigureAwait(true);
        return new BrainProvision(stamped, true);
    }

    /// <inheritdoc />
    public Task<BrainProvision> ProvisionOwner(Member owner)
        => EnsureBrain(owner);

    /// <inheritdoc />
    public Task<bool> IsRegistered(string principalId)
        => Task.FromResult(State.ToAccountBook().IsRegistered(principalId));

    /// <inheritdoc />
    public async Task<BrainProvision> RegisterPrincipal(string principalId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var book = State.ToAccountBook();
        var (nextBook, provision, facts) = book.RegisterPrincipal(principalId, displayName, _clock.GetUtcNow());
        if (!provision.NewBrain)
        {
            return provision;
        }

        var next = State.With(nextBook);
        var brain = new BrainRow { BrainId = provision.Member.BrainId, AccountId = provision.Member.AccountId, Kind = BrainKind.Personal };
        next = next with
        {
            Brains = [.. next.Brains, brain],
            Members = [.. next.Members, provision.Member],
        };
        await Save(next, facts).ConfigureAwait(true);
        return provision;
    }

    /// <inheritdoc />
    public Task<BrainProvision?> CompleteRegistration(string principalId)
        => Task.FromResult(State.ToAccountBook().CompleteRegistration(principalId));

    /// <inheritdoc />
    public Task<BrainEntry[]> BrainsOf(string principalId)
        => Task.FromResult(State.BrainsOf(principalId));

    /// <inheritdoc />
    public Task<BrainKind> KindOf(string brainScopeId)
        => Task.FromResult(State.BrainOf(brainScopeId)?.Kind ?? BrainKind.Personal);

    /// <inheritdoc />
    public Task<bool> BrainExists(string brainScopeId)
        => Task.FromResult(State.BrainOf(brainScopeId) is { } brain
            && (!string.IsNullOrEmpty(brain.AccountId) || State.OwnerMemberOf(brainScopeId) is not null));

    /// <inheritdoc />
    public Task<Member?> Membership(string brainScopeId, string principalId)
        => Task.FromResult(State.MemberOf(brainScopeId, principalId));

    /// <inheritdoc />
    public async Task<Member?> EnsureMember(Member member)
    {
        var caller = Stamped();
        ArgumentNullException.ThrowIfNull(member);

        var brain = State.Brain(member.BrainId);
        var outcome = brain.AddMember(caller, member, _clock.GetUtcNow());
        var nextBrain = outcome.OrThrow(caller, member.BrainId);

        if (outcome.Changed)
        {
            await Save(State.With(nextBrain), outcome.Facts).ConfigureAwait(true);
        }

        return nextBrain.MemberOf(member.PrincipalId);
    }

    /// <inheritdoc />
    public async Task RemoveMember(string brainScopeId, string accountId)
    {
        var caller = Stamped();
        var brain = State.Brain(brainScopeId);
        var outcome = brain.RemoveMember(caller, accountId);
        var nextBrain = outcome.OrThrow(caller, brainScopeId);

        if (outcome.Changed)
        {
            await Save(State.With(nextBrain), outcome.Facts).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public async Task RemovePrincipalMember(string brainScopeId, string principalId)
    {
        var caller = Stamped();
        var brain = State.Brain(brainScopeId);
        var outcome = brain.RemovePrincipal(caller, principalId);
        var nextBrain = outcome.OrThrow(caller, brainScopeId);

        if (outcome.Changed)
        {
            await Save(State.With(nextBrain), outcome.Facts).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public async Task<CallDecision?> Authorize(CallRequest request)
    {
        var caller = Stamped();
        ArgumentNullException.ThrowIfNull(request);

        var brain = State.Brain(caller.Brain.Value);
        var (outcome, decision) = brain.Authorize(caller, request);

        if (outcome.Changed)
        {
            await Save(State.With(outcome.Next), outcome.Facts).ConfigureAwait(true);
        }

        return decision;
    }

    /// <inheritdoc />
    public Task<Grant[]> ListGrants(string brainScopeId)
    {
        var now = _clock.GetUtcNow();
        return Task.FromResult(State.Grants
            .Where(g => g.WorkspaceId == brainScopeId && !g.Revoked && (g.Expires is null || g.Expires.Value > now))
            .ToArray());
    }

    /// <inheritdoc />
    public async Task<Grant?> GrantAsOwner(string brainId, Grant grant)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(grant);

        var brain = State.Brain(brainId);
        var now = _clock.GetUtcNow();
        if (grant.IsCanonical)
        {
            if (!brain.OwnedBy(caller))
            {
                return null;
            }

            var normalized = Grant.Normalize(grant);
            var stored = normalized with
            {
                Owner = string.IsNullOrEmpty(normalized.Owner) ? $"brain:{brainId}" : normalized.Owner,
                WorkspaceId = brainId,
                GrantedAt = now,
                Revoked = false,
            };
            var remaining = brain.Grants.Where(existing =>
            {
                var n = Grant.Normalize(existing);
                return !string.Equals(n.Id, stored.Id, StringComparison.Ordinal)
                    && !(string.Equals(n.Grantee, stored.Grantee, StringComparison.Ordinal)
                        && string.Equals(n.Target, stored.Target, StringComparison.Ordinal)
                        && string.Equals(n.Scope, stored.Scope, StringComparison.Ordinal));
            }).ToImmutableArray();
            var updatedBrain = brain with { Grants = remaining.Add(stored) };
            await Save(State.With(updatedBrain), new GrantRecorded(stored)).ConfigureAwait(true);
            return stored;
        }

        var outcome = brain.RecordGrant(caller, grant, now);
        if (outcome.Refused == Refusal.NotOwner)
        {
            return null;
        }

        var nextBrain = outcome.OrThrow(caller, brainId);
        if (outcome.Changed)
        {
            await Save(State.With(nextBrain), outcome.Facts).ConfigureAwait(true);
        }

        return nextBrain.Grants.FirstOrDefault(g => g.AppId == grant.AppId && g.SemanticTypeId == grant.SemanticTypeId && g.Mode == grant.Mode && g.WorkspaceId == brainId && !g.Revoked);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var brain = State.Brain(brainId);
        var outcome = brain.RevokeGrant(caller, appId, semanticTypeId, mode);
        if (outcome.Refused == Refusal.NotOwner)
        {
            return false;
        }

        var nextBrain = outcome.OrThrow(caller, brainId);
        if (outcome.Changed)
        {
            await Save(State.With(nextBrain), outcome.Facts).ConfigureAwait(true);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeGrantAsOwner(string brainId, string grantId)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);

        var brain = State.Brain(brainId);
        if (!brain.OwnedBy(caller))
        {
            return false;
        }

        var remaining = brain.Grants.Where(g => !string.Equals(g.Id, grantId, StringComparison.Ordinal)).ToImmutableArray();
        if (remaining.Length != brain.Grants.Length)
        {
            await Save(State.With(brain with { Grants = remaining }), new DirectoryChanged(brainId)).ConfigureAwait(true);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentNullException.ThrowIfNull(grants);

        if (State.BrainOf(brainScopeId) is null)
        {
            return [];
        }

        var brain = State.Brain(brainScopeId);
        var now = _clock.GetUtcNow();
        var added = new List<Grant>();
        foreach (var grant in grants)
        {
            var normalized = Grant.Normalize(grant);
            var alreadyHeld = brain.Grants.Concat(added).Select(Grant.Normalize).Any(held => !held.Revoked
                && string.Equals(held.Grantee, normalized.Grantee, StringComparison.Ordinal)
                && string.Equals(held.Target, normalized.Target, StringComparison.Ordinal)
                && string.Equals(held.Scope, normalized.Scope, StringComparison.Ordinal));
            if (alreadyHeld)
            {
                continue;
            }

            added.Add(normalized with
            {
                Owner = $"brain:{brainScopeId}",
                WorkspaceId = brainScopeId,
                GrantedAt = now,
                Revoked = false,
            });
        }

        if (added.Count == 0)
        {
            return [];
        }

        var updatedBrain = brain with { Grants = [.. brain.Grants, .. added] };
        await Save(State.With(updatedBrain), new DirectoryChanged(brainScopeId)).ConfigureAwait(true);
        return [.. added];
    }

    /// <inheritdoc />
    public Task<DirectoryBrainSnapshot> SnapshotOf(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return Task.FromResult(State.SnapshotOf(brainId));
    }

    /// <inheritdoc />
    public async Task<bool> SpendOnce(string brainId, string[] grantIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(grantIds);

        if (grantIds.Length == 0)
        {
            return false;
        }

        var brain = State.Brain(brainId);
        var live = brain.Grants
            .Where(g => !g.Revoked)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!grantIds.All(live.Contains))
        {
            return false;
        }

        var spent = grantIds.ToHashSet(StringComparer.Ordinal);
        var remaining = brain.Grants.Where(g => !spent.Contains(g.Id)).ToImmutableArray();
        await Save(State.With(brain with { Grants = remaining }), new DirectoryChanged(brainId)).ConfigureAwait(true);
        return true;
    }

    /// <inheritdoc />
    public async Task<PrincipalLinkStatus> LinkPrincipal(string principalId, string linkedPrincipalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkedPrincipalId);

        if (State.PrincipalOf(principalId) is not { DefaultMembership: { } membership })
        {
            return PrincipalLinkStatus.NoSuchPrincipal;
        }

        if (State.PrincipalOf(linkedPrincipalId) is { DefaultMembership: { } existing })
        {
            return existing.AccountId == membership.AccountId && existing.BrainId == membership.BrainId
                ? PrincipalLinkStatus.AlreadyLinked
                : PrincipalLinkStatus.Conflict;
        }

        var members = State.Members.ToList();
        var sourceBrains = State.BrainsOf(principalId);
        foreach (var entry in sourceBrains)
        {
            if (State.MemberOf(entry.Id, principalId) is { } sourceMember
                && !members.Any(m => m.BrainId == entry.Id && m.PrincipalId == linkedPrincipalId))
            {
                members.Add(sourceMember with { PrincipalId = linkedPrincipalId });
            }
        }

        var linkedRecord = new PrincipalRecord(
            linkedPrincipalId,
            membership with { PrincipalId = linkedPrincipalId },
            registrationComplete: true);
        var principals = State.Principals
            .Where(p => !string.Equals(p.PrincipalId, linkedPrincipalId, StringComparison.Ordinal))
            .Append(linkedRecord)
            .ToArray();

        var next = State with
        {
            Principals = principals,
            Members = [.. members],
        };
        await Save(next, new DirectoryChanged(membership.BrainId)).ConfigureAwait(true);
        return PrincipalLinkStatus.Linked;
    }

    /// <inheritdoc />
    public async Task<BrainProvision> ClaimBrain(string brainScopeId, string principalId, string displayName, Grant[]? carriedGrants = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        if (State.OwnerMemberOf(brainScopeId) is { } existingOwner)
        {
            if (!string.Equals(existingOwner.PrincipalId, principalId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("This brain is already claimed by another principal.");
            }

            return new BrainProvision(existingOwner, false);
        }

        var now = _clock.GetUtcNow();
        var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName) ? principalId : displayName.Trim();
        var existingPrincipal = State.PrincipalOf(principalId);
        var book = State.ToAccountBook();
        string accountId;
        var accountFacts = ImmutableArray<Signal>.Empty;

        if (existingPrincipal?.DefaultMembership is { } existingDefault)
        {
            accountId = existingDefault.AccountId;
            (book, _, accountFacts) = book.EnsureAccount(accountId, existingDefault.DisplayName, principalId, existingDefault.JoinedAt, now);
        }
        else
        {
            accountId = Guid.NewGuid().ToString("N");
            (book, _, accountFacts) = book.EnsureAccount(accountId, effectiveDisplayName, principalId, now, now);
        }

        var member = new Member
        {
            PrincipalId = principalId,
            AccountId = accountId,
            BrainId = brainScopeId,
            DisplayName = effectiveDisplayName,
            Role = MemberRole.Owner,
            JoinedAt = now,
        };

        var next = State.With(book);
        var brains = next.BrainOf(brainScopeId) is null
            ? [.. next.Brains, new BrainRow { BrainId = brainScopeId, AccountId = accountId, Kind = BrainKind.Personal }]
            : next.Brains.Select(b => b.BrainId == brainScopeId && string.IsNullOrEmpty(b.AccountId) ? b with { AccountId = accountId } : b).ToArray();
        var members = next.Members
            .Where(m => !(m.BrainId == brainScopeId && m.PrincipalId == principalId))
            .Append(member)
            .ToArray();

        var grants = next.Grants.ToList();
        if (carriedGrants is { Length: > 0 })
        {
            var brainGrants = grants.Where(g => g.WorkspaceId == brainScopeId).ToList();
            foreach (var grant in carriedGrants)
            {
                var normalized = Grant.Normalize(grant);
                var duplicate = brainGrants.Select(Grant.Normalize).Any(held => !held.Revoked
                    && string.Equals(held.Grantee, normalized.Grantee, StringComparison.Ordinal)
                    && string.Equals(held.Target, normalized.Target, StringComparison.Ordinal)
                    && string.Equals(held.Scope, normalized.Scope, StringComparison.Ordinal));
                if (duplicate)
                {
                    continue;
                }

                var stampedGrant = normalized with
                {
                    Owner = $"brain:{brainScopeId}",
                    WorkspaceId = brainScopeId,
                    GrantedAt = now,
                    Revoked = false,
                };
                brainGrants.Add(stampedGrant);
                grants.Add(stampedGrant);
            }
        }

        var updatedPrincipal = new PrincipalRecord(
            principalId,
            existingPrincipal?.DefaultMembership ?? member,
            registrationComplete: true);
        var principals = next.Principals
            .Where(p => !string.Equals(p.PrincipalId, principalId, StringComparison.Ordinal))
            .Append(updatedPrincipal)
            .ToArray();

        next = next with
        {
            Brains = brains,
            Members = members,
            Grants = [.. grants],
            Principals = principals,
        };

        var facts = ImmutableArray.Create<Signal>(
            [.. accountFacts,
            new BrainCreated(brainScopeId, accountId, BrainKind.Personal),
            new MemberAdded(member)]);
        await Save(next, facts).ConfigureAwait(true);
        return new BrainProvision(member, true);
    }

    private Caller Stamped() => Context.Caller ?? throw new UnauthorizedAccessException("The directory acts only on a stamped caller.");
}
