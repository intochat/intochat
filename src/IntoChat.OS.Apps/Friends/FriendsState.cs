using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Friends;

// What is open from Shared with me: a folder's entries, or one file's text.
public sealed record FriendsOpenedShare(
    string OwnerBrain,
    string ShareId,
    string Path,
    bool CanEdit,
    SharedEntry[] Entries,
    string? Text = null,
    string SavedBy = "",
    DateTimeOffset? SavedAt = null);

/// <summary>
/// Immutable DDD Aggregate state for <see cref="FriendsApp"/> (<c>app.friends</c>).
/// </summary>
public sealed record FriendsAppState(
    string Handle,
    string DisplayName,
    string Bio,
    string[] PinnedApps,
    ProfileFriend[] Friends,
    ProfilePostItem[] Posts,
    string SelectedScope,
    string Status,
    string FriendCode = "",
    WorkspaceShare[]? SharedByMe = null,
    SharedPointer[]? SharedWithMe = null,
    FriendsOpenedShare? Opened = null)
{
    public const int MinHandleLength = 2;
    public const int MaxHandleLength = 32;

    public static readonly string[] SupportedScopes = ["all", "public", "friends"];
    public static readonly string[] SupportedTabs = ["all", "feed", "roster", "shared"];
    public static readonly string[] SupportedCircles = ["public", "friends", "close-friends", "team"];
    public static readonly string[] SupportedShareRoles = ["viewer", "editor"];

    public FriendsAppState() : this("user", "User", "", [], [], [], "all", "Ready") { }

    public string ActiveTab { get; init; } = "all";

    public string PendingInviteCode { get; init; } = "";

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("SelectedScope is always all, public, or friends."), JsonIgnore]
    public bool ValidScope => SupportedScopes.Contains(SelectedScope, StringComparer.Ordinal);

    [Invariant("ActiveTab is always all, feed, roster, or shared."), JsonIgnore]
    public bool ValidActiveTab => SupportedTabs.Contains(ActiveTab, StringComparer.Ordinal);

    [Invariant("Profile handle is non-empty and well-formed."), JsonIgnore]
    public bool HandlePresent => IsValidHandleFormat(Handle);

    [Invariant("Friends roster contains no duplicate contacts and every confirmed friend has an account identifier."), JsonIgnore]
    public bool ValidAndUniqueFriends =>
        Friends is not null
        && Friends.All(static f => !string.IsNullOrWhiteSpace(f.Handle) && (!f.Confirmed || !string.IsNullOrWhiteSpace(f.Account)))
        && Friends.Select(FriendIdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Friends.Length;

    [Invariant("Every published App-Post carries a non-empty ID and valid circle scope."), JsonIgnore]
    public bool ValidPostsInvariant =>
        Posts is not null
        && Posts.All(static p => !string.IsNullOrWhiteSpace(p.Id) && SupportedCircles.Contains(p.Scope, StringComparer.OrdinalIgnoreCase));

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedScope(string? scope) => Decision.Rule(
        "invalid_scope",
        scope,
        static s => string.IsNullOrWhiteSpace(s) || !SupportedScopes.Contains(s.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Feed scope must be all, public, or friends.");

    public static Rule SupportedTabRule(string? tab) => Decision.Rule(
        "invalid_tab",
        tab,
        static t => string.IsNullOrWhiteSpace(t) || !SupportedTabs.Contains(t.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Active view tab must be all, feed, roster, or shared.");

    public static Rule ValidHandleRule(string? handle) => Decision.Rule(
        "invalid_handle",
        handle,
        static h => !IsValidHandleFormat(NormalizeHandleToken(h)),
        $"Handle must be {MinHandleLength}–{MaxHandleLength} letters, digits, '.', '_', or '-'.");

    public static Rule ValidInviteCodeRule(string? code) => Decision.Rule(
        "invalid_invite_code",
        code,
        static c => !IsValidInviteCodeFormat(c),
        "Enter a valid friend invite code (for example 'ada.XXXX-XXXX-XXXX').");

    public static Rule ValidCircleRule(string? circle) => Decision.Rule(
        "invalid_circle",
        circle,
        static c => string.IsNullOrWhiteSpace(c) || !SupportedCircles.Contains(c.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Circle must be public, friends, close-friends, or team.");

    public static Rule ValidShareRoleRule(string? role) => Decision.Rule(
        "invalid_share_role",
        role,
        static r => string.IsNullOrWhiteSpace(r) || !SupportedShareRoles.Contains(r.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Share role must be viewer or editor.");

    public static Rule ValidSharePathRule(string? path) => Decision.Rule(
        "invalid_share_path",
        path,
        static p => string.IsNullOrWhiteSpace(p),
        "Workspace folder or file path is required to share.");

    public Rule NonDuplicateContactRule(string? handleOrAccount) => Decision.Rule(
        "duplicate_contact",
        (Self: Handle, Roster: Friends ?? [], Candidate: NormalizeHandleToken(handleOrAccount)),
        static x => !string.IsNullOrEmpty(x.Candidate) && (
            string.Equals(x.Self, x.Candidate, StringComparison.OrdinalIgnoreCase)
            || x.Roster.Any(f =>
                string.Equals(f.Handle, x.Candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Account, x.Candidate, StringComparison.OrdinalIgnoreCase))),
        static x => string.Equals(x.Self, x.Candidate, StringComparison.OrdinalIgnoreCase)
            ? $"Cannot add your own handle '@{x.Candidate}' as a friend."
            : $"Contact '@{x.Candidate}' is already in your friends list.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public Decision<FriendsAppState> SelectScope([Sample("all", "public", "friends", "secret")] string scope)
    {
        if (Decision.Check("filter-scope", SupportedScope(scope)) is { } refusal)
        {
            return refusal;
        }

        var normalized = scope.Trim().ToLowerInvariant();
        return Decision.Accept(this with
        {
            SelectedScope = normalized,
            Status = $"Filtered feed to {normalized} posts.",
        });
    }

    public Decision<FriendsAppState> SelectTab([Sample("all", "feed", "roster", "shared", "unknown-tab")] string tab)
    {
        if (Decision.Check("select-tab", SupportedTabRule(tab)) is { } refusal)
        {
            return refusal;
        }

        var normalized = tab.Trim().ToLowerInvariant();
        return Decision.Accept(this with
        {
            ActiveTab = normalized,
            Status = $"Viewing {normalized} section.",
        });
    }

    public Decision<FriendsAppState> UpdateProfileDraft(
        [Sample("ada", "bad handle!")] string handle,
        [Sample("Ada Lovelace")] string displayName = "User",
        [Sample("Analytical Engine builder")] string bio = "")
    {
        if (Decision.Check("update-bio", ValidHandleRule(handle)) is { } refusal)
        {
            return refusal;
        }

        var cleanHandle = NormalizeHandleToken(handle);
        var cleanDisplay = string.IsNullOrWhiteSpace(displayName) ? cleanHandle : displayName.Trim();
        return Decision.Accept(this with
        {
            Handle = cleanHandle,
            DisplayName = cleanDisplay,
            Bio = bio?.Trim() ?? string.Empty,
            Status = $"Updated personal bio for @{cleanHandle}.",
        });
    }

    public Decision<FriendsAppState> StageInviteCode([Sample("ada.CODE-2026", "bad")] string code)
    {
        var senderHandle = ExtractCodeSenderHandle(code);
        if (Decision.Check("redeem-code", ValidInviteCodeRule(code), NonDuplicateContactRule(senderHandle)) is { } refusal)
        {
            return refusal;
        }

        var trimmed = code.Trim();
        return Decision.Accept(this with
        {
            PendingInviteCode = trimmed,
            Status = $"Validated invite code from @{senderHandle}.",
        });
    }

    public Decision<FriendsAppState> AddContactToCircle(
        [Sample("ada", "user")] string handle,
        [Sample("Ada Lovelace")] string displayName = "Ada",
        [Sample("friends", "invalid-circle")] string circle = "friends")
    {
        if (Decision.Check(
                "add-contact",
                ValidHandleRule(handle),
                NonDuplicateContactRule(handle),
                ValidCircleRule(circle)) is { } refusal)
        {
            return refusal;
        }

        var cleanHandle = NormalizeHandleToken(handle);
        var cleanCircle = circle.Trim().ToLowerInvariant();
        var cleanName = string.IsNullOrWhiteSpace(displayName) ? cleanHandle : displayName.Trim();
        var added = new ProfileFriend(
            BrainId: $"{cleanHandle}-brain",
            Handle: cleanHandle,
            DisplayName: cleanName,
            Scope: cleanCircle,
            AddedAt: default,
            Account: $"{cleanHandle}-account",
            Confirmed: true);

        var currentFriends = Friends ?? [];
        return Decision.Accept(this with
        {
            Friends = [.. currentFriends, added],
            PendingInviteCode = string.Empty,
            Status = $"@{cleanHandle} joined circle '{cleanCircle}'.",
        });
    }

    // ── Normalization & Profile Projection Helpers ───────────────────────────

    public static string NormalizeScope(string? scope, string fallback = "all") => scope?.Trim().ToLowerInvariant() switch
    {
        "public" => "public",
        "friends" => "friends",
        "all" => "all",
        _ => fallback is "public" or "friends" ? fallback : "all",
    };

    public static string NormalizeCircle(string? circle, string fallback = "public") => circle?.Trim().ToLowerInvariant() switch
    {
        "public" => "public",
        "friends" => "friends",
        "close-friends" => "close-friends",
        "team" => "team",
        _ => SupportedCircles.Contains(fallback, StringComparer.OrdinalIgnoreCase) ? fallback.ToLowerInvariant() : "public",
    };

    public static string NormalizeHandleToken(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return string.Empty;
        }

        return handle.Trim().TrimStart('@');
    }

    public static bool IsValidHandleFormat(string? handle)
    {
        var clean = NormalizeHandleToken(handle);
        if (clean.Length < MinHandleLength || clean.Length > MaxHandleLength)
        {
            return false;
        }

        foreach (var ch in clean)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidInviteCodeFormat(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Trim();
        if (trimmed.Length < 6 || trimmed.Length > 96 || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return trimmed.Contains('.', StringComparison.Ordinal) || trimmed.Contains('-', StringComparison.Ordinal);
    }

    [IgnoreExplore]
    public FriendsAppState ApplyProfile(ProfileState profile, string status, string? selectedScope = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var cleanHandle = IsValidHandleFormat(profile.Handle) ? NormalizeHandleToken(profile.Handle) : "user";
        var cleanDisplay = string.IsNullOrWhiteSpace(profile.DisplayName) ? cleanHandle : profile.DisplayName;
        var dedupedFriends = (profile.Friends ?? [])
            .Where(static f => !string.IsNullOrWhiteSpace(f.Handle))
            .Select(static f => f.Confirmed && string.IsNullOrWhiteSpace(f.Account)
                ? f with { Account = $"{NormalizeHandleToken(f.Handle)}-account" }
                : f)
            .GroupBy(FriendIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.Last())
            .ToArray();
        var validPosts = (profile.Posts ?? [])
            .Where(static p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(static p => SupportedCircles.Contains(p.Scope, StringComparer.OrdinalIgnoreCase)
                ? p
                : p with { Circle = "public" })
            .ToArray();

        return this with
        {
            Handle = cleanHandle,
            DisplayName = cleanDisplay,
            Bio = profile.Bio ?? "",
            PinnedApps = profile.PinnedApps ?? [],
            Friends = dedupedFriends,
            Posts = validPosts,
            SelectedScope = NormalizeScope(selectedScope, SelectedScope),
            Status = status,
        };
    }

    private static string FriendIdentityKey(ProfileFriend friend)
        => !string.IsNullOrWhiteSpace(friend.Account) ? friend.Account : friend.Handle;

    private static string ExtractCodeSenderHandle(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var trimmed = code.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? NormalizeHandleToken(trimmed[..dot]) : string.Empty;
    }

    public override string ToString()
        => $"@{Handle} · {ActiveTab}/{SelectedScope} · {Friends?.Length ?? 0} friend(s)";
}
