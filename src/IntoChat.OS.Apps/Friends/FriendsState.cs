using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Friends;

public sealed record SocialFeedItem(
    string OwnerBrain,
    string PostId,
    string Author,
    string Text,
    PostReaction[] Reactions,
    PostComment[] Comments,
    long Sequence,
    DateTimeOffset CreatedAt,
    // What a post with an app carries, and whether it is this person's own post. Absent in a feed stored before the app
    // showed one list of posts.
    string? Title = null,
    string Audience = "",
    string? AppId = null,
    PostEmbeddedApp[]? EmbeddedApps = null,
    string? ForkedFrom = null,
    bool Mine = false)
{
    [JsonIgnore]
    public bool HasApp => Title is not null || AppId is not null || EmbeddedApps is { Length: > 0 };
}

public sealed record FriendRoomMessage(
    string EntryId,
    string Author,
    string Text,
    DateTimeOffset At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SenderIdentity? Sender = null);

public sealed record ActiveFriendRoom(
    string OwnerBrain,
    string RoomKey,
    long Revision,
    FriendRoomMessage[] Messages,
    string[] PresentAccounts,
    string Title = "",
    ThreadKind Kind = ThreadKind.SharedRoom,
    PinnedScreen[]? PinnedScreens = null,
    string[]? Participants = null);

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
    // Not filled any more: the screen shows one list of posts, SocialFeed, read from the post store (OWED.md).
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

    public FriendsAppState() : this("user", "User", "", [], [], "all", "Ready") { }

    public string ActiveTab { get; init; } = "all";

    // Display projection refreshed from IFriends.List() on Open and after each roster act; never the source of truth.
    public Friend[] Roster { get; init; } = [];

    public string SocialPostDraft { get; init; } = "";
    public SocialFeedItem[] SocialFeed { get; init; } = [];
    public string RoomMessageDraft { get; init; } = "";
    public string NewRoomTitleDraft { get; init; } = "";
    public ThreadEntry[] Rooms { get; init; } = [];
    public ActiveFriendRoom? ActiveRoom { get; init; }

    [IgnoreExplore]
    public FriendsAppState SetSocialPostDraft(string? draft) => this with { SocialPostDraft = draft ?? "" };

    [IgnoreExplore]
    public FriendsAppState SetRoomMessageDraft(string? draft) => this with { RoomMessageDraft = draft ?? "" };

    [IgnoreExplore]
    public FriendsAppState SetNewRoomTitleDraft(string? draft) => this with { NewRoomTitleDraft = draft ?? "" };

    // What the person typed into the friend-code, share-path and shared-text fields, kept until the request that uses it.
    public string CodeDraft { get; init; } = "";
    public string PathDraft { get; init; } = "";
    public string SharedTextDraft { get; init; } = "";

    [IgnoreExplore]
    public FriendsAppState SetCodeDraft(string? draft) => this with { CodeDraft = draft ?? "" };

    [IgnoreExplore]
    public FriendsAppState SetPathDraft(string? draft) => this with { PathDraft = draft ?? "" };

    [IgnoreExplore]
    public FriendsAppState SetSharedTextDraft(string? draft) => this with { SharedTextDraft = draft ?? "" };

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("SelectedScope is always all, public, or friends."), JsonIgnore]
    public bool ValidScope => SupportedScopes.Contains(SelectedScope, StringComparer.Ordinal);

    [Invariant("ActiveTab is always all, feed, roster, or shared."), JsonIgnore]
    public bool ValidActiveTab => SupportedTabs.Contains(ActiveTab, StringComparer.Ordinal);

    [Invariant("Profile handle is non-empty and well-formed."), JsonIgnore]
    public bool HandlePresent => IsValidHandleFormat(Handle);

    [Invariant("Friends roster contains no duplicate accounts and every entry names an account."), JsonIgnore]
    public bool ValidAndUniqueRoster =>
        Roster is not null
        && Roster.All(static f => !string.IsNullOrWhiteSpace(f.Account.Value))
        && Roster.Select(static f => f.Account.Value).Distinct(StringComparer.Ordinal).Count() == Roster.Length;

    [Invariant("Every published App-Post carries a non-empty ID and an audience."), JsonIgnore]
    public bool ValidPostsInvariant =>
        Posts is not null
        && Posts.All(static p => !string.IsNullOrWhiteSpace(p.Id) && p.Seen is not null);

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

    [IgnoreExplore]
    public FriendsAppState ApplyProfile(ProfileState profile, string status, string? selectedScope = null, Friend[]? roster = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var cleanHandle = IsValidHandleFormat(profile.Handle) ? NormalizeHandleToken(profile.Handle) : "user";
        var cleanDisplay = string.IsNullOrWhiteSpace(profile.DisplayName) ? cleanHandle : profile.DisplayName;
        var dedupedRoster = roster is null
            ? Roster ?? []
            : roster
                .Where(static f => !string.IsNullOrWhiteSpace(f.Account.Value))
                .GroupBy(static f => f.Account.Value, StringComparer.Ordinal)
                .Select(static g => g.Last())
                .ToArray();

        return this with
        {
            Handle = cleanHandle,
            DisplayName = cleanDisplay,
            Bio = profile.Bio ?? "",
            PinnedApps = profile.PinnedApps ?? [],
            Posts = [],
            Roster = dedupedRoster,
            SelectedScope = NormalizeScope(selectedScope, SelectedScope),
            Status = status,
        };
    }

    public override string ToString()
        => $"@{Handle} · {ActiveTab}/{SelectedScope} · {Roster?.Length ?? 0} friend(s)";
}
