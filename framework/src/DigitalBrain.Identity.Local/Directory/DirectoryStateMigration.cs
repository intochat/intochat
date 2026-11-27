namespace DigitalBrain.Identity.Local.Directory;

using DigitalBrain.Capabilities.Identity;

/// <summary>Snapshot of a single brain's directory state for local observers and policy evaluators.</summary>
public sealed record DirectoryBrainSnapshot
{
    /// <summary>The brain identifier.</summary>
    public string BrainId { get; init; } = "";

    /// <summary>The owning account identifier, or null when unowned.</summary>
    public string? AccountId { get; init; }

    /// <summary>Members of this brain.</summary>
    public Member[] Members { get; init; } = [];

    /// <summary>Grants recorded in this brain.</summary>
    public Grant[] Grants { get; init; } = [];

    /// <summary>Whether an owner member has been provisioned for this brain.</summary>
    public bool OwnerProvisioned { get; init; }

    /// <summary>The brain kind.</summary>
    public BrainKind Kind { get; init; }
}

/// <summary>Outcome of linking an alias principal to an existing principal's account and brains.</summary>
public enum PrincipalLinkStatus
{
    /// <summary>The principal was linked and added to all of the source principal's brains.</summary>
    Linked = 0,

    /// <summary>The principal was already linked to the same account and default brain.</summary>
    AlreadyLinked = 1,

    /// <summary>The source principal does not exist in the directory.</summary>
    NoSuchPrincipal = 2,

    /// <summary>The linked principal already signs in to a different account or brain; nothing was changed.</summary>
    Conflict = 3,
}

/// <summary>Optional host observer notified when <see cref="DirectoryNeuron"/> activates or commits state.</summary>
public interface IDirectoryChangeObserver
{
    /// <summary>Called on activation (after any one-time state migration) and after each committed state change.</summary>
    void OnDirectoryChanged(DirectoryState state);
}

/// <summary>
/// Additive one-time migration from legacy nested brain rows (<c>BrainRow.Members</c>, <c>BrainRow.Grants</c>,
/// <c>BrainRow.OwnerProvisioned</c>) to the flat <see cref="DirectoryState"/> layout. Idempotent: once legacy
/// fields are lifted and cleared, subsequent activations perform zero writes.
/// </summary>
public static class DirectoryStateMigration
{
    /// <summary>
    /// Checks whether <paramref name="state"/> contains any legacy nested brain-row fields and, if so, produces
    /// a migrated <see cref="DirectoryState"/> preserving every account, principal, brain, membership, grant,
    /// and operation.
    /// </summary>
    public static bool TryMigrate(DirectoryState state, out DirectoryState migrated)
    {
        ArgumentNullException.ThrowIfNull(state);

        var needsMigration = false;
        foreach (var brain in state.Brains)
        {
            if (brain.Members is not null || brain.Grants is not null || brain.OwnerProvisioned is not null)
            {
                needsMigration = true;
                break;
            }
        }

        if (!needsMigration)
        {
            migrated = state;
            return false;
        }

        var members = new List<Member>(state.Members);
        var seenMembers = new HashSet<(string BrainId, string PrincipalId)>();
        foreach (var m in members)
        {
            seenMembers.Add((m.BrainId, m.PrincipalId));
        }

        var grants = new List<Grant>(state.Grants);
        var seenGrantIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in grants)
        {
            if (!string.IsNullOrEmpty(g.Id))
            {
                seenGrantIds.Add(g.Id);
            }
        }

        var cleanedBrains = new BrainRow[state.Brains.Length];
        for (var i = 0; i < state.Brains.Length; i++)
        {
            var brain = state.Brains[i];
            string? fallbackAccountId = null;

            if (brain.Members is { Length: > 0 })
            {
                foreach (var member in brain.Members)
                {
                    var normalizedMember = string.IsNullOrEmpty(member.BrainId)
                        ? member with { BrainId = brain.BrainId }
                        : member;

                    if (normalizedMember.Role == MemberRole.Owner && !string.IsNullOrEmpty(normalizedMember.AccountId))
                    {
                        fallbackAccountId ??= normalizedMember.AccountId;
                    }

                    if (seenMembers.Add((normalizedMember.BrainId, normalizedMember.PrincipalId)))
                    {
                        members.Add(normalizedMember);
                    }
                }
            }

            if (brain.Grants is { Length: > 0 })
            {
                foreach (var grant in brain.Grants)
                {
                    var normalizedGrant = string.IsNullOrEmpty(grant.WorkspaceId)
                        ? grant with { WorkspaceId = brain.BrainId }
                        : grant;

                    if (!string.IsNullOrEmpty(normalizedGrant.Id) && !seenGrantIds.Add(normalizedGrant.Id))
                    {
                        continue;
                    }

                    grants.Add(normalizedGrant);
                }
            }

            cleanedBrains[i] = new BrainRow
            {
                BrainId = brain.BrainId,
                AccountId = !string.IsNullOrEmpty(brain.AccountId)
                    ? brain.AccountId
                    : (fallbackAccountId ?? ""),
                Kind = brain.Kind,
            };
        }

        migrated = state with
        {
            Brains = cleanedBrains,
            Members = [.. members],
            Grants = [.. grants],
        };
        return true;
    }
}
