/*
# Friends
Role: Default
Shipped: true
Operations: open, publish, update-bio, make-code, redeem-code, unfriend, share-folder, unshare, open-shared, save-shared, fork-post, fork_app, filter-scope, publish-social-post, refresh-feed, react-to-post, open-friend-room, send-room-message, ui.command

Personal social profile, friends, files shared with friends, and interactive App-Post feed.
A friend is confirmed with a code: one person makes it, the other redeems it. A folder or file of the workspace is shared
with a confirmed friend as Viewer or Editor; what friends shared appears under Shared with me.
Each Post is an App<TState> template that renders inline in the profile/feed, can embed child apps via NeuronRef slots, and can be forked/remixed into the viewer's brain.
*/
using IntoChat.OS.Host.Apps;
using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Friends;

[Signal("friends.open")]
public sealed record FriendsOpen(string? Scope = null) : Signal;

[Signal("friends.publish")]
public sealed record FriendsPublish(string? Content = null, string Scope = "public", string? Id = null, string? Title = null, string? TemplateAppId = null, string? EmbeddedAppIds = null) : Signal;

[Signal("friends.update-bio")]
public sealed record FriendsUpdateBio(string Handle = "user", string Bio = "", string[]? PinnedApps = null, string DisplayName = "") : Signal;

[Signal("friends.make-code")]
public sealed record FriendsMakeCode : Signal;

// An empty value is read from the field of the same name on the screen.
[Signal("friends.redeem-code")]
public sealed record FriendsRedeemCode(string Code = "") : Signal;

[Signal("friends.unfriend")]
public sealed record FriendsUnfriend(string Account = "") : Signal;

[Signal("friends.share-folder")]
public sealed record FriendsShareFolder(string Account = "", string Path = "", string Role = "viewer") : Signal;

[Signal("friends.unshare")]
public sealed record FriendsUnshare(string ShareId = "") : Signal;

[Signal("friends.open-shared")]
public sealed record FriendsOpenShared(string OwnerBrain = "", string ShareId = "", string Path = "") : Signal;

[Signal("friends.save-shared")]
public sealed record FriendsSaveShared(string OwnerBrain = "", string ShareId = "", string Path = "", string Text = "") : Signal;

[Signal("friends.fork_app")]
public sealed record FriendsForkApp(string PostId = "post-1") : Signal;

[Signal("friends.fork-post")]
public sealed record FriendsForkPost(string PostId = "post-1", string? NewPostId = null, string? RemixTitle = null) : Signal;

[Signal("friends.filter-scope")]
public sealed record FriendsFilterScope(string Scope = "all") : Signal;

[Signal("friends.publish-social-post")]
public sealed record PublishSocialPost(string Text = "") : Signal;

[Signal("friends.refresh-feed")]
public sealed record RefreshFeed() : Signal;

[Signal("friends.react-to-post")]
public sealed record ReactToPost(string OwnerBrain = "", string PostId = "", string Reaction = "like") : Signal;

[Signal("friends.open-friend-room")]
public sealed record OpenFriendRoom(string RoomKey = "", string FriendAccount = "", string RoomOwnerBrain = "") : Signal;

[Signal("friends.send-room-message")]
public sealed record SendRoomMessage(string RoomOwnerBrain = "", string RoomKey = "", string Text = "") : Signal;

[Neuron("app.friends"), Screen("app.friends", Title = "Friends", Icon = "users", Category = "Social", Dock = 9)]
public sealed partial class FriendsApp : AggregateScreen<FriendsAppState>
{
    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        return base.OnActivate(cancellationToken);
    }

    public static AppBundle Bundle => AppBundle.FromScreen<FriendsApp>(
        "friends",
        "2.0.0",
        description: "Personal profile, friends confirmed with a code, files shared with friends, and interactive App-Post templates.",
        operations:
        [
            new("open", "Show personal profile, friends list, and interactive app-post feed."),
            new("publish", "Publish an interactive App-Post template with optional embedded child apps."),
            new("update-bio", "Update personal profile handle, display name, bio, and pinned apps."),
            new("make-code", "Make a friend code to send to someone."),
            new("redeem-code", "Add a friend with the code they sent."),
            new("unfriend", "Remove a friend and everything shared between you."),
            new("share-folder", "Share a folder or file of the workspace with a confirmed friend as viewer or editor."),
            new("unshare", "Stop sharing a folder or file."),
            new("open-shared", "Open a folder or file a friend shared."),
            new("save-shared", "Save a text file a friend shared for editing."),
            new("fork-post", "Fork an interactive App-Post template (and its embedded app slots) into a new post."),
            new("fork_app", "Fork and install an App-Post unit template into Creator and submit to IInstalls consent loop."),
            new("filter-scope", "Filter the feed by scope (all, public, friends)."),
            new("publish-social-post", "Publish a social post visible to confirmed friends."),
            new("refresh-feed", "Refresh social posts from yourself and confirmed friends."),
            new("react-to-post", "Add a reaction to a social post."),
            new("open-friend-room", "Create, invite to, or join a live friend chat room."),
            new("send-room-message", "Send a chat message to the active friend room."),
            new(UiCommand.SignalAlias, "Dispatch a typed Friends aggregate command."),
        ]);

    public new FriendsAppState State => base.State;
    public View Render() => PreviewView(State);

    private readonly TimeProvider? _time;

    public FriendsApp() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public FriendsApp(TimeProvider? time = null) => _time = time;

    private DateTimeOffset NowUtc()
    {
        if (_time is not null) { return _time.GetUtcNow(); }
        try { return Context.Time.GetUtcNow(); }
        catch (InvalidOperationException) { return TimeProvider.System.GetUtcNow(); }
    }

    [On]
    public async Task Open(FriendsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await Profile.Read();
        var roster = await TryReadRosterAsync();
        var scope = FriendsAppState.NormalizeScope(request.Scope, State.SelectedScope);
        var opened = State.ApplyProfile(profile, $"@{profile.Handle} · {roster.Length} friends · {profile.Posts.Length} app-posts", scope, roster);
        await Show(await WithShares(opened));
    }

    [On]
    public async Task Publish(FriendsPublish request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.Scope) && FriendsAppState.ValidCircleRule(request.Scope).Broken)
        {
            await ApplyDecisionAsync(Decision.Check("publish", FriendsAppState.ValidCircleRule(request.Scope))!);
            return;
        }

        var content = string.IsNullOrWhiteSpace(request.Content) ? "New App-Post" : request.Content.Trim();
        var scope = FriendsAppState.NormalizeCircle(request.Scope, "public");
        var audience = Audience.FromLegacy(scope);
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"post-{Guid.NewGuid():N}" : request.Id.Trim();
        var templateAppId = string.IsNullOrWhiteSpace(request.TemplateAppId) ? $"post.app.{id}" : request.TemplateAppId.Trim().ToLowerInvariant();
        var embedded = ParseEmbeddedApps(request.EmbeddedAppIds, CurrentBrainId());
        var (unitSource, unitFiles) = await TryReadUnitTemplateAsync(templateAppId);

        var updated = await Profile.PublishAppPost(new ProfilePostItem(
            Id: id,
            Content: content,
            CreatedAt: NowUtc(),
            Circle: "",
            Title: request.Title ?? "",
            TemplateAppId: templateAppId,
            TemplateSource: unitSource,
            UnitFiles: unitFiles,
            EmbeddedApps: embedded)
        {
            Audience = audience,
        });

        await Show(State.ApplyProfile(updated, $"Published app-post {id} ({scope}) with {embedded.Length} embedded app(s)."));
    }

    [On]
    public async Task UpdateBio(FriendsUpdateBio request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawHandle = string.IsNullOrWhiteSpace(request.Handle) ? State.Handle : request.Handle.Trim();
        var draftDecision = State.UpdateProfileDraft(rawHandle, request.DisplayName, request.Bio);
        if (draftDecision.IsRefused)
        {
            await ApplyDecisionAsync(draftDecision);
            return;
        }

        var handle = FriendsAppState.NormalizeHandleToken(rawHandle);
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? State.DisplayName : request.DisplayName.Trim();
        var updated = await Profile.UpdateProfile(handle, displayName, request.Bio?.Trim() ?? string.Empty, request.PinnedApps ?? State.PinnedApps ?? []);
        await Show(State.ApplyProfile(updated, $"Updated personal bio for @{handle}."));
    }

    public const string CodeField = "friend-code";
    public const string PathField = "share-path";
    public const string TextField = "shared-text";
    public static string FieldKey(string field) => $"apps/friends/{field}";

    [Slot(Key = "profile")] private partial IProfile Profile { get; }
    [Slot(Key = FriendsDefaults.KeyPart)] private partial IFriends Friends { get; }
    [Slot(Key = FileSharesDefaults.KeyPart)] private partial IFileShares Door { get; }
    [Slot(Key = SharedWithMeDefaults.KeyPart)] private partial ISharedWithMe Mine { get; }
    [Slot(Required = false)] private partial ISynapseStudio? SynapseStudio { get; }
    [Slot(Required = false)] private partial IPosts? SocialPosts { get; }
    [Slot(Required = false)] private partial SlotCollection<IRoom> SocialRooms { get; }
    [Slot(Required = false)] private partial IRooms? RoomIndex { get; }
    [Slot(Key = "apps/friends/" + CodeField)] private partial IntoChat.OS.Modules.Flutter.Fields.IField CodeFieldSlot { get; }
    [Slot(Key = "apps/friends/" + PathField)] private partial IntoChat.OS.Modules.Flutter.Fields.IField PathFieldSlot { get; }
    [Slot(Key = "apps/friends/" + TextField)] private partial IntoChat.OS.Modules.Flutter.Fields.IField TextFieldSlot { get; }

    [On]
    public Task MakeCode(FriendsMakeCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("make a friend code", async () =>
        {
            var code = await Friends.Mint();
            return State with { FriendCode = code.Value, Status = "Send this code to your friend. It works once, for 24 hours." };
        });
    }

    [On]
    public Task RedeemCode(FriendsRedeemCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("add that friend", async () =>
        {
            var raw = await Typed(request.Code, CodeFieldSlot);
            if (!IntoChat.OS.Code.TryParse(raw, out var code))
            {
                return State with
                {
                    Status = raw.Contains('.', StringComparison.Ordinal)
                        ? "That code is from before the update; ask for a new one."
                        : "That is not a valid friend code.",
                };
            }
            var friend = await Friends.Redeem(code);
            var profile = await Profile.Read();
            var roster = await TryReadRosterAsync();
            return await WithShares(State.ApplyProfile(profile, $"{friend.Name} is now a friend.", roster: roster));
        });
    }

    [On]
    public Task Unfriend(FriendsUnfriend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("remove that friend", async () =>
        {
            await Friends.Unfriend(new IntoChat.OS.Modules.Identity.Contracts.AccountId(request.Account));
            var profile = await Profile.Read();
            var roster = await TryReadRosterAsync();
            return await WithShares(State.ApplyProfile(profile, "Removed the friend and everything shared between you.", roster: roster) with { Opened = null });
        });
    }

    [On]
    public Task ShareFolder(FriendsShareFolder request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("share that", async () =>
        {
            var path = await Typed(request.Path, PathFieldSlot);
            var role = string.Equals(request.Role?.Trim(), "editor", StringComparison.OrdinalIgnoreCase) ? ShareRole.Editor : ShareRole.Viewer;
            var share = await Door.Share(request.Account, path, role);
            return await WithShares(State with { Status = $"Shared {share.Path} as {share.Role}." });
        });
    }

    [On]
    public Task Unshare(FriendsUnshare request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("stop sharing that", async () =>
        {
            await Door.Unshare(request.ShareId);
            return await WithShares(State with { Status = "Stopped sharing." });
        });
    }

    [On]
    public Task OpenShared(FriendsOpenShared request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("open that", async () =>
        {
            var pointer = (State.SharedWithMe ?? []).FirstOrDefault(p => p.OwnerBrain == request.OwnerBrain && p.ShareId == request.ShareId)
                ?? throw new UnauthorizedAccessException("That is not shared with you.");
            var entries = await Mine.List(request.OwnerBrain, request.ShareId, request.Path);
            // One entry at the path itself is a file: show its text. Anything else is a folder listing.
            var file = entries is [{ IsFolder: false } only] && (request.Path.Length == 0 ? !pointer.IsFolder : only.Path == request.Path) ? only : null;
            var text = file is null ? null : System.Text.Encoding.UTF8.GetString(await Mine.Read(request.OwnerBrain, request.ShareId, request.Path) ?? []);
            var opened = new FriendsOpenedShare(request.OwnerBrain, request.ShareId, request.Path, pointer.Role == ShareRole.Editor, file is null ? entries : [], text, file?.ModifiedBy ?? "", file?.Modified);
            return State with { Opened = opened, Status = file is null ? $"Opened {pointer.Path}/{request.Path}".TrimEnd('/') : $"Opened {file.Name}." };
        });
    }

    [On]
    public Task SaveShared(FriendsSaveShared request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("save that", async () =>
        {
            var text = await Typed(request.Text, TextFieldSlot);
            var saved = await Mine.Write(request.OwnerBrain, request.ShareId, request.Path, System.Text.Encoding.UTF8.GetBytes(text));
            var opened = State.Opened is { } open && open.ShareId == request.ShareId && open.Path == request.Path
                ? open with { Text = text, SavedBy = saved.ModifiedBy, SavedAt = saved.Modified }
                : State.Opened;
            var when = saved.Modified is { } at && at != default ? $" at {at:u}" : "";
            return State with { Opened = opened, Status = $"Saved {saved.Name}. Last saved by {saved.ModifiedBy}{when}." };
        });
    }

    public Task MakeCodeAsync() => MakeCode(new FriendsMakeCode());
    public Task RedeemTypedCodeAsync() => RedeemCode(new FriendsRedeemCode());
    public Task UnfriendAsync(string account) => Unfriend(new FriendsUnfriend(account));
    public Task ShareTypedPathAsync(string account, string role) => ShareFolder(new FriendsShareFolder(account, "", role));
    public Task UnshareAsync(string shareId) => Unshare(new FriendsUnshare(shareId));
    public Task OpenSharedAsync(string ownerBrain, string shareId, string path) => OpenShared(new FriendsOpenShared(ownerBrain, shareId, path));
    public Task SaveTypedTextAsync(string ownerBrain, string shareId, string path) => SaveShared(new FriendsSaveShared(ownerBrain, shareId, path));

    // Runs one friend or sharing step. A refusal or a mistake in what was typed leaves the screen as it was and says why.
    private async Task Attempt(string what, Func<Task<FriendsAppState>> step)
    {
        try { await Show(await step()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<FriendsApp>().LogSwallowed(error, "A friends or sharing step was refused or failed.");
            await Show(State with { Status = $"Could not {what}: {error.Message}" });
        }
    }

    private async Task<Friend[]> TryReadRosterAsync()
    {
        try { return await Friends.List(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<FriendsApp>().LogSwallowed(error, "Friends roster could not be read.");
            return State.Roster ?? [];
        }
    }

    // What is shared by this person and with them. Sharing may be unavailable (no friends yet, or a host without it);
    // the profile and feed still show.
    private async Task<FriendsAppState> WithShares(FriendsAppState state)
    {
        try { return state with { SharedByMe = await Door.Shares(), SharedWithMe = await Mine.Refresh() }; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<FriendsApp>().LogSwallowed(error, "Shares could not be read.");
            return state with { SharedByMe = state.SharedByMe ?? [], SharedWithMe = state.SharedWithMe ?? [] };
        }
    }

    // The value a signal carried, or what the person typed into the field of that name.
    private static async Task<string> Typed(string? carried, IntoChat.OS.Modules.Flutter.Fields.IField field)
        => !string.IsNullOrWhiteSpace(carried) ? carried.Trim() : ((await field.Read()).Value ?? "").Trim();

    [On]
    public async Task ForkApp(FriendsForkApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var postId = string.IsNullOrWhiteSpace(request.PostId) ? "post-1" : request.PostId.Trim();
        var post = (await Profile.Read()).Posts.FirstOrDefault(p => string.Equals(p.Id, postId, StringComparison.OrdinalIgnoreCase));
        if (post is null) { await Show(State with { Status = $"Post {postId} not found to fork." }); return; }

        var alias = post.TemplateAppId;
        var files = post.UnitFiles ?? ShippedUnits.Files(alias);
        if (files.Count == 0) { await Show(State with { Status = $"Post {postId} has no unit files to install into Creator." }); return; }

        if (SynapseStudio is not { } studio)
        {
            await Show(State with { Status = $"Could not fork app {alias}: Synapse studio is unavailable." });
            return;
        }

        try
        {
            await studio.DeployFiles(alias, files, new Dictionary<string, string>(StringComparer.Ordinal));
            await Show(State with { Status = $"Forked app-post template {alias} into Creator; ready for consent." });
        }
        catch (Exception error)
        {
            OsConsoleLog.For<FriendsApp>().LogSwallowed(error, "Failed to deploy forked app-post files into Synapse Studio.");
            await Show(State with { Status = $"Could not fork app {alias}: {error.Message}" });
        }
    }

    [On]
    public async Task ForkPost(FriendsForkPost request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var postId = string.IsNullOrWhiteSpace(request.PostId) ? "post-1" : request.PostId.Trim();
        var newId = string.IsNullOrWhiteSpace(request.NewPostId) ? $"post-fork-{Guid.NewGuid():N}" : request.NewPostId.Trim();
        var updated = await Profile.ForkPost(postId, newId, request.RemixTitle);
        await Show(State.ApplyProfile(updated, $"Forked and remixed app-post {postId} as {newId}."));
    }

    [On]
    public Task FilterScope(FriendsFilterScope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetScope = string.IsNullOrWhiteSpace(request.Scope) ? "all" : request.Scope;
        return ApplyDecisionAsync(State.SelectScope(targetScope));
    }

    [On, Description("Publish a social post visible to confirmed friends.")]
    public Task PublishSocial(PublishSocialPost request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("publish social post", async () =>
        {
            if (SocialPosts is not { } posts)
            {
                return State with { Status = "Social posts are unavailable." };
            }

            var text = string.IsNullOrWhiteSpace(request.Text) ? State.SocialPostDraft : request.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return State with { Status = "Post text is required." };
            }

            var roster = await TryReadRosterAsync();
            var audience = roster
                .Select(static f => f.Account.Value)
                .Where(static a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            await posts.Publish(new PostDraft(text.Trim(), audience));
            var feed = await LoadSocialFeedAsync(roster);
            return State with
            {
                Roster = roster,
                SocialPostDraft = "",
                SocialFeed = feed,
                Status = $"Published social post ({feed.Length} post(s) in feed).",
            };
        });
    }

    [On, Description("Refresh social posts from yourself and confirmed friends.")]
    public Task RefreshSocialFeed(RefreshFeed request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("refresh social feed", async () =>
        {
            var roster = await TryReadRosterAsync();
            var feed = await LoadSocialFeedAsync(roster);
            return State with
            {
                Roster = roster,
                SocialFeed = feed,
                Status = $"Refreshed social feed ({feed.Length} post(s)).",
            };
        });
    }

    [On, Description("React to a social post.")]
    public Task ReactToSocialPost(ReactToPost request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("react to post", async () =>
        {
            if (string.IsNullOrWhiteSpace(request.PostId) || string.IsNullOrWhiteSpace(request.Reaction))
            {
                return State with { Status = "Post ID and reaction are required." };
            }

            var ownerBrain = string.IsNullOrWhiteSpace(request.OwnerBrain)
                ? CurrentBrainId().Value
                : request.OwnerBrain.Trim();
            var posts = string.Equals(ownerBrain, CurrentBrainId().Value, StringComparison.Ordinal)
                ? SocialPosts
                : Slots.Offered<IPosts>(new BrainId(ownerBrain), new NeuronId(new BrainId(ownerBrain), "social.posts", IPosts.DefaultKey));

            if (posts is null)
            {
                return State with { Status = "Social posts are unavailable." };
            }

            await posts.React(request.PostId.Trim(), request.Reaction.Trim());
            var roster = await TryReadRosterAsync();
            var feed = await LoadSocialFeedAsync(roster);
            return State with
            {
                Roster = roster,
                SocialFeed = feed,
                Status = $"Reacted '{request.Reaction.Trim()}' to post.",
            };
        });
    }

    [On, Description("Create, invite a friend to, or join a live chat room.")]
    public Task OpenRoom(OpenFriendRoom request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("open friend room", async () =>
        {
            if (string.IsNullOrWhiteSpace(request.RoomKey))
            {
                return State with { Status = "Room key is required." };
            }

            var roomKey = request.RoomKey.Trim();
            var ownBrain = CurrentBrainId();
            var ownerBrainValue = string.IsNullOrWhiteSpace(request.RoomOwnerBrain)
                ? ownBrain.Value
                : request.RoomOwnerBrain.Trim();
            var isOwnRoom = string.Equals(ownerBrainValue, ownBrain.Value, StringComparison.Ordinal);

            var roster = await TryReadRosterAsync();
            var room = ResolveRoom(ownerBrainValue, roomKey, isOwnRoom);
            if (room is null)
            {
                return State with { Status = "Social rooms are unavailable." };
            }

            if (isOwnRoom && !string.IsNullOrWhiteSpace(request.FriendAccount))
            {
                var friendAccount = request.FriendAccount.Trim();
                var friend = roster.FirstOrDefault(f =>
                    string.Equals(f.Account.Value, friendAccount, StringComparison.OrdinalIgnoreCase));
                if (friend is not null)
                {
                    await room.Invite(friend.Account.Value, friend.Brain, Role.Editor);
                }
            }

            await room.Here();
            var view = await room.Read();
            var roomId = new NeuronId(new BrainId(ownerBrainValue), "social.room", roomKey);
            if (RoomIndex is { } index)
            {
                _ = await index.LastRevision(roomId);
            }

            return State with
            {
                Roster = roster,
                ActiveRoom = new ActiveFriendRoom(ownerBrainValue, roomKey, view.Revision, view.Messages, view.PresentAccounts),
                Status = isOwnRoom ? $"Opened room '{roomKey}'." : $"Joined room '{roomKey}'.",
            };
        });
    }

    [On, Description("Send a chat message in the active friend room.")]
    public Task SendRoomChat(SendRoomMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("send room message", async () =>
        {
            var ownBrain = CurrentBrainId();
            var ownerBrainValue = string.IsNullOrWhiteSpace(request.RoomOwnerBrain)
                ? State.ActiveRoom?.OwnerBrain ?? ownBrain.Value
                : request.RoomOwnerBrain.Trim();
            var roomKey = string.IsNullOrWhiteSpace(request.RoomKey)
                ? State.ActiveRoom?.RoomKey ?? ""
                : request.RoomKey.Trim();
            var text = string.IsNullOrWhiteSpace(request.Text) ? State.RoomMessageDraft : request.Text;

            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return State with { Status = "Open a room before sending a message." };
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return State with { Status = "Message text is required." };
            }

            var isOwnRoom = string.Equals(ownerBrainValue, ownBrain.Value, StringComparison.Ordinal);
            var room = ResolveRoom(ownerBrainValue, roomKey, isOwnRoom);
            if (room is null)
            {
                return State with { Status = "Social rooms are unavailable." };
            }

            await room.Say(text.Trim());
            await room.Here();
            var view = await room.Read();

            return State with
            {
                RoomMessageDraft = "",
                ActiveRoom = new ActiveFriendRoom(ownerBrainValue, roomKey, view.Revision, view.Messages, view.PresentAccounts),
                Status = $"Sent message to room '{roomKey}'.",
            };
        });
    }

    private IRoom? ResolveRoom(string ownerBrainValue, string roomKey, bool isOwnRoom)
    {
        if (isOwnRoom)
        {
            return SocialRooms.TryGet(roomKey);
        }

        var ownerBrain = new BrainId(ownerBrainValue);
        return Slots.Offered<IRoom>(ownerBrain, new NeuronId(ownerBrain, "social.room", roomKey));
    }

    private async Task<SocialFeedItem[]> LoadSocialFeedAsync(Friend[] roster)
    {
        var items = new List<SocialFeedItem>();
        var ownBrain = CurrentBrainId().Value;

        if (SocialPosts is { } ownPosts)
        {
            try
            {
                var page = await ownPosts.Since(0);
                foreach (var post in page.Posts)
                {
                    items.Add(new SocialFeedItem(ownBrain, post.Id, post.Author, post.Text, post.Reactions, post.Comments, post.Sequence, post.CreatedAt));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<FriendsApp>().LogSwallowed(error, "Own social posts could not be read.");
            }
        }

        foreach (var friend in roster)
        {
            if (string.IsNullOrWhiteSpace(friend.Brain.Value) || string.Equals(friend.Brain.Value, ownBrain, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var friendPosts = Slots.Offered<IPosts>(friend.Brain, new NeuronId(friend.Brain, "social.posts", IPosts.DefaultKey));
                var page = await friendPosts.Since(0);
                foreach (var post in page.Posts)
                {
                    items.Add(new SocialFeedItem(friend.Brain.Value, post.Id, post.Author, post.Text, post.Reactions, post.Comments, post.Sequence, post.CreatedAt));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<FriendsApp>().LogSwallowed(error, "Friend social posts could not be read (no offer or unavailable).");
            }
        }

        return items
            .OrderByDescending(static p => p.CreatedAt)
            .ThenByDescending(static p => p.Sequence)
            .ToArray();
    }

    public Task PublishQuickPostAsync() => Publish(new FriendsPublish("Interactive revenue & sketch board", "friends"));
    public Task ForkAndInstallPostAsync(string postId) => ForkApp(new FriendsForkApp(postId));
    public Task ForkPostTemplateAsync(string postId) => ForkPost(new FriendsForkPost(postId));

    private BrainId CurrentBrainId()
    {
        try { return Id.Brain; }
        catch (InvalidOperationException ex)
        {
            OsConsoleLog.For<FriendsApp>().LogSwallowed(ex, "FriendsApp Id.Brain unavailable outside neuron activation.");
            return default;
        }
    }

    private async Task<(string Source, IReadOnlyDictionary<string, string>? Files)> TryReadUnitTemplateAsync(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || SynapseStudio is not { } studio) { return ("", null); }
        try
        {
            var csFiles = await studio.SourceFiles(alias);
            if (csFiles.Count == 0)
            {
                var shipped = ShippedUnits.Files(alias);
                if (shipped.Count == 0) { return ("", null); }
                return (string.Join("\n\n", shipped.Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)), shipped);
            }
            var files = new Dictionary<string, string>(csFiles, StringComparer.Ordinal);
            foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
            return (string.Join("\n\n", csFiles.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)), files);
        }
        catch (Exception ex)
        {
            OsConsoleLog.For<FriendsApp>().LogSwallowed(ex, "Failed to read unit template for alias.");
            return ("", null);
        }
    }

    private static string AppUri(BrainId brain, string appId)
        => IntoChat.OS.Host.Primitives.AppRef.TryOf(brain, appId, out var appRef) ? appRef.Key : $"apps/{appId}";

    private static PostEmbeddedApp[] ParseEmbeddedApps(string? rawEmbeddedApps, BrainId brain) =>
        string.IsNullOrWhiteSpace(rawEmbeddedApps)
            ? [new("canvas", "markdraw", "Markdraw Canvas", AppUri(brain, "markdraw")), new("analytics", "data", "Data Studio", AppUri(brain, "data"))]
            : [.. rawEmbeddedApps
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((appId, idx) =>
                {
                    var clean = appId.ToLowerInvariant();
                    return new PostEmbeddedApp($"slot-{idx + 1}", clean, $"{char.ToUpperInvariant(clean[0]) + clean[1..]} App", AppUri(brain, clean));
                })];

    protected override Node Render(FriendsAppState state)
    {
        var roster = state.Roster ?? [];
        var allPosts = state.Posts ?? [];
        var visiblePosts = state.SelectedScope switch
        {
            "public" => allPosts.Where(static p => p.Seen is Audience.Public).ToArray(),
            "friends" => allPosts.Where(static p => p.Seen is Audience.Friends).ToArray(),
            _ => allPosts,
        };

        var showRoster = state.ActiveTab is "all" or "roster";
        var showShared = state.ActiveTab is "all" or "shared";
        var showFeed = state.ActiveTab is "all" or "feed";

        return VStack(Space.Md)[
            LastRejection is not null
                ? Banner("Action refused", string.Join(" ", LastRejection.Messages), tone: "error")
                : null,
            HeaderBar(
                $"{state.DisplayName} (@{state.Handle})",
                string.IsNullOrWhiteSpace(state.Bio) ? state.Status : $"{state.Bio} · {state.Status}",
                StatusPill(StatusKind.Done, $"{roster.Length} friends"),
                PrimaryButton("Publish App-Post", Icons.Plus).Key("publish-app-post").Small().OnClick(() => PublishQuickPostAsync())),
            HStack(Space.Sm)[
                Segmented(
                    "Section",
                    state.ActiveTab,
                    Option("all", "Overview").Key("friends-tab:all").OnSelect<FriendsAppState>(x => x.SelectTab("all")),
                    Option("feed", "App-Posts").Key("friends-tab:feed").OnSelect<FriendsAppState>(x => x.SelectTab("feed")),
                    Option("roster", "Roster").Key("friends-tab:roster").OnSelect<FriendsAppState>(x => x.SelectTab("roster")),
                    Option("shared", "Shared").Key("friends-tab:shared").OnSelect<FriendsAppState>(x => x.SelectTab("shared"))),
                FlexSpacer(),
                Tabs(
                    state.SelectedScope,
                    Tab("all", "All Posts", "layout-grid").Key("friends-scope:all").OnSelect<FriendsAppState>(x => x.SelectScope("all")),
                    Tab("public", "Public", "globe").Key("friends-scope:public").OnSelect<FriendsAppState>(x => x.SelectScope("public")),
                    Tab("friends", "Friends", "users").Key("friends-scope:friends").OnSelect<FriendsAppState>(x => x.SelectScope("friends")))
            ],
            Grid(3, Space.Sm)[
                StatCard("kpi-friends-posts", "App-Posts", $"{allPosts.Length}", $"@{state.Handle}", BadgeTone.Accent, "layout-grid"),
                StatCard("kpi-friends-count", "Friends", $"{roster.Length}", "Confirmed", BadgeTone.Done, "users"),
                StatCard("kpi-friends-embedded", "Embedded Apps", $"{allPosts.Sum(p => (p.EmbeddedApps ?? []).Length)}", $"{(state.PinnedApps ?? []).Length} Pinned", BadgeTone.Accent, "cpu")
            ],
            showRoster ? RenderFriends(state, roster) : null,
            showFeed ? RenderSocialFeed(state) : null,
            showRoster || showFeed ? RenderFriendRoom(state) : null,
            showShared ? RenderSharedByMe(state) : null,
            showShared ? RenderSharedWithMe(state) : null,
            showFeed
                ? (visiblePosts.Length == 0
                    ? Empty("No App-Posts in this scope", "Publish an interactive App-Post or switch feed scope.")
                    : VStack(Space.Md, [.. visiblePosts.Select(post => RenderPostCard(state, post))]).ToNode())
                : null
        ];
    }

    private Node RenderSocialFeed(FriendsAppState state)
    {
        var feed = state.SocialFeed ?? [];
        var postRows = feed.Select(item =>
        {
            var reactionsSummary = item.Reactions is { Length: > 0 }
                ? $" · {item.Reactions.Length} reaction(s)"
                : "";
            return (Node)HStack(Space.Sm)[
                Text($"{item.Author}: {item.Text}{reactionsSummary}"),
                Button("Like", "heart")
                    .Key($"btn:react-like:{item.OwnerBrain}:{item.PostId}")
                    .Small()
                    .Sends(Sends(new ReactToPost(item.OwnerBrain, item.PostId, "like")))
            ];
        });

        return Card("Social Feed", "Publish updates to your friends and pull their latest posts.")[
            VStack(Space.Sm,
            [
                HStack(Space.Sm)[
                    TextField("Social Post", "Share an update with friends")
                        .Key("input:social-post")
                        .Value(state.SocialPostDraft)
                        .OnChange<FriendsAppState>(s => s.SetSocialPostDraft(Arg.Text)),
                    PrimaryButton("Publish Post", "send")
                        .Key("btn:social-publish")
                        .Small()
                        .Sends(Sends(new PublishSocialPost(state.SocialPostDraft))),
                    GhostButton("Refresh Feed", Icons.RefreshCw)
                        .Key("btn:social-refresh-feed")
                        .Small()
                        .Sends(Sends(new RefreshFeed()))
                ],
                .. postRows,
            ])
        ];
    }

    private Node RenderFriendRoom(FriendsAppState state)
    {
        var room = state.ActiveRoom;
        var subtitle = room is null
            ? "Open a room with a friend to chat live."
            : $"Room '{room.RoomKey}' · Present: {(room.PresentAccounts.Length > 0 ? string.Join(", ", room.PresentAccounts) : "nobody yet")}";

        var messageNodes = room is { Messages.Length: > 0 }
            ? room.Messages.Select(m => (Node)Text($"{m.Author}: {m.Text}")).ToArray()
            : [];

        return Card("Room Chat", subtitle)[
            VStack(Space.Sm,
            [
                .. messageNodes,
                HStack(Space.Sm)[
                    TextField("Room Message", "Say something in the room")
                        .Key("input:room-message")
                        .Value(state.RoomMessageDraft)
                        .OnChange<FriendsAppState>(s => s.SetRoomMessageDraft(Arg.Text)),
                    PrimaryButton("Send", "send")
                        .Key("btn:room-send")
                        .Small()
                        .Sends(Sends(new SendRoomMessage(room?.OwnerBrain ?? "", room?.RoomKey ?? "", state.RoomMessageDraft)))
                ],
            ])
        ];
    }

    // Friends, how to add one, and the path to share. Every entry in IFriends is confirmed.
    private Node RenderFriends(FriendsAppState state, Friend[] roster)
    {
        var rows = roster.Select(friend => (Node)HStack(Space.Sm,
        [
            new TagPill($"{friend.Name} (@{friend.Account.Value})", "accent", "users"),
            Button("Chat", Icons.MessageSquare).Key($"btn:open-room:{friend.Account.Value}").Small().Sends(Sends(new OpenFriendRoom("chat", friend.Account.Value))),
            Button("Join Room", "door-open").Key($"btn:join-room:{friend.Brain.Value}:chat").Small().Sends(Sends(new OpenFriendRoom("chat", friend.Account.Value, friend.Brain.Value))),
            Button("Share as viewer", "eye").Key($"share-viewer:{friend.Account.Value}").Small().OnClick(() => ShareTypedPathAsync(friend.Account.Value, "viewer")),
            Button("Share as editor", "pencil").Key($"share-editor:{friend.Account.Value}").Small().OnClick(() => ShareTypedPathAsync(friend.Account.Value, "editor")),
            GhostButton("Remove", "user-minus").Key($"unfriend:{friend.Account.Value}").Small().OnClick(() => UnfriendAsync(friend.Account.Value)),
        ]));

        return Card("Friends", "A friend is confirmed with a code: one of you makes it, the other adds it.")[
            VStack(Space.Sm,
            [
                .. rows,
                HStack(Space.Sm)[
                    Button("Make a friend code", "key").Key("make-code").Small().OnClick(() => MakeCodeAsync()),
                    Text(state.FriendCode)
                ],
                TextField(FieldKey(CodeField), "XXXX-XXXX-XXXX").Key(CodeField).Label("Friend code you received"),
                PrimaryButton("Add friend", "user-plus").Key("redeem-code").Small().OnClick(() => RedeemTypedCodeAsync()),
                TextField(FieldKey(PathField), "/work/folder").Key(PathField).Label("Folder or file of your workspace to share"),
            ])
        ];
    }

    private Node? RenderSharedByMe(FriendsAppState state)
    {
        var shares = state.SharedByMe ?? [];
        if (shares.Length == 0) { return null; }
        return Card("Shared by me", "People with access to your files. Removing takes effect at once.")[
            VStack(Space.Xs, [.. shares.Select(share => (Node)HStack(Space.Sm)[
                Text(share.Broken ? $"{share.Path} · {share.Account} · {share.Role} · Broken" : $"{share.Path} · {share.Account} · {share.Role}"),
                GhostButton("Remove", "x").Key($"unshare:{share.Id}").Small().OnClick(() => UnshareAsync(share.Id))
            ])])
        ];
    }

    private Node RenderSharedWithMe(FriendsAppState state)
    {
        var pointers = state.SharedWithMe ?? [];
        if (pointers.Length == 0)
        {
            return Empty("Nothing shared with you", "When a friend shares a folder or file with you, it appears here.");
        }

        var list = pointers.Select(pointer => (Node)HStack(Space.Sm)[
            Text($"{pointer.OwnerAccount} · {pointer.Path} · {pointer.Role}"),
            Button("Open", "folder-open").Key($"open-shared:{pointer.ShareId}").Small().OnClick(() => OpenSharedAsync(pointer.OwnerBrain, pointer.ShareId, ""))
        ]);

        return Card("Shared with me", "The file stays with its owner. It is reachable while their brain is running; the last save wins.")[
            VStack(Space.Sm, [.. list, RenderOpened(state.Opened)])
        ];
    }

    private Node? RenderOpened(FriendsOpenedShare? opened)
    {
        if (opened is null) { return null; }
        if (opened.Text is null)
        {
            return VStack(Space.Xs, [.. opened.Entries.Select(entry => (Node)HStack(Space.Sm)[
                Text(entry.IsFolder ? $"{entry.Path}/" : entry.Path),
                Button("Open", entry.IsFolder ? "folder-open" : "file-text").Key($"open-entry:{opened.ShareId}:{entry.Path}").Small()
                    .OnClick(() => OpenSharedAsync(opened.OwnerBrain, opened.ShareId, entry.Path))
            ])]);
        }

        var byAndWhen = string.IsNullOrEmpty(opened.SavedBy)
            ? opened.Path
            : opened.SavedAt is { } at && at != default
                ? $"{opened.Path} · last saved by {opened.SavedBy} at {at:u}"
                : $"{opened.Path} · last saved by {opened.SavedBy}";

        return VStack(Space.Xs,
        [
            MutedText(byAndWhen),
            opened.CanEdit
                ? TextField(FieldKey(TextField), "").Key(TextField).Label("Text").Value(opened.Text)
                : Text(opened.Text),
            .. opened.CanEdit
                ? new Node[] { PrimaryButton("Save", "save").Key($"save-shared:{opened.ShareId}").Small().OnClick(() => SaveTypedTextAsync(opened.OwnerBrain, opened.ShareId, opened.Path)) }
                : [],
        ]);
    }

    private static string AudienceLabel(Audience audience) => audience switch
    {
        Audience.Public => "public",
        Audience.Friends => "friends",
        Audience.Only => "only",
        _ => "only",
    };

    private Node RenderPostCard(FriendsAppState state, ProfilePostItem post)
    {
        var audience = AudienceLabel(post.Seen);
        var embedded = post.EmbeddedApps ?? [];
        RecordFieldNode[] fields =
        [
            new("Author", $"@{state.Handle}"),
            new("Audience", audience),
            new("App Template", post.TemplateAppId),
            new("Summary", post.Content),
            .. !string.IsNullOrWhiteSpace(post.ForkedFrom) ? [new RecordFieldNode("Forked From", post.ForkedFrom)] : Array.Empty<RecordFieldNode>(),
            .. embedded.Select(child => new RecordFieldNode($"Embedded [{child.Slot}]", $"{child.Title} ({child.TargetNeuronUri})")),
        ];

        return Card(string.IsNullOrWhiteSpace(post.Title) ? $"App-Post · {post.Id}" : post.Title, $"@{state.Handle} · [{audience}] · {post.TemplateAppId}")[
            VStack(Space.Sm,
            [
                Text(post.Content),
                new RecordCardNode(
                    $"App-Post · {audience}",
                    post.Id,
                    fields,
                    Subtitle: post.Content,
                    LinkLabel: "Fork App-Post",
                    Open: Button("Fork App-Post").Key($"fork-{post.Id}").OnClick(() => ForkPostTemplateAsync(post.Id))),
                .. embedded.Select(child => (Node)Kit.Slot(child.TargetNeuronUri)),
                HStack(Space.Sm)[
                    Button("Fork & Install", "download").Key($"fork-and-install:{post.Id}").Small().OnClick(() => ForkAndInstallPostAsync(post.Id)),
                    Button("Fork App Template", "git-branch").Key($"fork-app-template:{post.Id}").Small().OnClick(() => ForkPostTemplateAsync(post.Id)),
                    GhostButton("Open in Creator", "wand-sparkles").Key($"open-in-creator:{post.Id}").Small().WithIntent(IntentKind.OpenWindow, "creator")
                ],
            ])
        ];
    }
}
