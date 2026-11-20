/*
# Friends
Role: Default
Shipped: true
Operations: open, publish, update-bio, make-code, redeem-code, unfriend, share-folder, unshare, open-shared, save-shared, fork-post, fork_app, filter-scope, ui.command

Personal social profile, friends, files shared with friends, and interactive App-Post feed.
A friend is confirmed with a code: one person makes it, the other redeems it. A folder or file of the workspace is shared
with a confirmed friend as Viewer or Editor; what friends shared appears under Shared with me.
Each Post is an App<TState> template that renders inline in the profile/feed, can embed child apps via NeuronRef slots, and can be forked/remixed into the viewer's brain.
*/
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
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

[Neuron("app.friends"), Screen("app.friends", Title = "Friends")]
public sealed class FriendsApp : AggregateScreen<FriendsAppState>
{
    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        return base.OnActivate(cancellationToken);
    }

    public static AppBundle Bundle { get; } = new(
        "friends",
        "2.0.0",
        new AppManifest(
            Title: "Friends",
            Description: "Personal profile, friends confirmed with a code, files shared with friends, and interactive App-Post templates.",
            Operations:
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
                new(UiCommand.SignalAlias, "Dispatch a typed Friends aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 9),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(FriendsApp).AssemblyQualifiedName,
    };

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
        var profile = await Get<IProfile>("profile").Read();
        var scope = FriendsAppState.NormalizeScope(request.Scope, State.SelectedScope);
        var opened = State.ApplyProfile(profile, $"@{profile.Handle} · {(profile.Friends ?? []).Length} friends · {profile.Posts.Length} app-posts", scope);
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
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"post-{Guid.NewGuid():N}" : request.Id.Trim();
        var templateAppId = string.IsNullOrWhiteSpace(request.TemplateAppId) ? $"post.app.{id}" : request.TemplateAppId.Trim().ToLowerInvariant();
        var embedded = ParseEmbeddedApps(request.EmbeddedAppIds, CurrentBrainId());
        var (unitSource, unitFiles) = await TryReadUnitTemplateAsync(templateAppId);

        var updated = await Get<IProfile>("profile").PublishAppPost(new ProfilePostItem(
            Id: id,
            Content: content,
            CreatedAt: NowUtc(),
            Circle: scope,
            Title: request.Title ?? "",
            TemplateAppId: templateAppId,
            TemplateSource: unitSource,
            UnitFiles: unitFiles,
            EmbeddedApps: embedded));

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
        var updated = await Get<IProfile>("profile").UpdateProfile(handle, displayName, request.Bio?.Trim() ?? string.Empty, request.PinnedApps ?? State.PinnedApps ?? []);
        await Show(State.ApplyProfile(updated, $"Updated personal bio for @{handle}."));
    }

    public const string CodeField = "friend-code";
    public const string PathField = "share-path";
    public const string TextField = "shared-text";
    public static string FieldKey(string field) => $"apps/friends/{field}";

    private IFriendLinks Links => Get<IFriendLinks>(FriendLinksDefaults.KeyPart);
    private IFileShares Door => Get<IFileShares>(FileSharesDefaults.KeyPart);
    private ISharedWithMe Mine => Get<ISharedWithMe>(SharedWithMeDefaults.KeyPart);

    [On]
    public Task MakeCode(FriendsMakeCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("make a friend code", async () =>
        {
            var code = await Links.Mint();
            return State with { FriendCode = code.Code, Status = $"Send this code to your friend. It works once, until {code.ExpiresAt:u}." };
        });
    }

    [On]
    public Task RedeemCode(FriendsRedeemCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("add that friend", async () =>
        {
            var code = await Typed(request.Code, CodeField);
            var friend = await Links.Redeem(code);
            var profile = await Get<IProfile>("profile").Read();
            return await WithShares(State.ApplyProfile(profile, $"@{friend.Handle} is now a friend."));
        });
    }

    [On]
    public Task Unfriend(FriendsUnfriend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("remove that friend", async () =>
        {
            await Links.Unfriend(request.Account);
            var profile = await Get<IProfile>("profile").Read();
            return await WithShares(State.ApplyProfile(profile, "Removed the friend and everything shared between you.") with { Opened = null });
        });
    }

    [On]
    public Task ShareFolder(FriendsShareFolder request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Attempt("share that", async () =>
        {
            var path = await Typed(request.Path, PathField);
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
            var text = await Typed(request.Text, TextField);
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
    private async Task<string> Typed(string? carried, string field)
        => !string.IsNullOrWhiteSpace(carried) ? carried.Trim() : ((await Get<IntoChat.OS.Modules.Flutter.Fields.IField>(FieldKey(field)).Read()).Value ?? "").Trim();

    [On]
    public async Task ForkApp(FriendsForkApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var postId = string.IsNullOrWhiteSpace(request.PostId) ? "post-1" : request.PostId.Trim();
        var post = (await Get<IProfile>("profile").Read()).Posts.FirstOrDefault(p => string.Equals(p.Id, postId, StringComparison.OrdinalIgnoreCase));
        if (post is null) { await Show(State with { Status = $"Post {postId} not found to fork." }); return; }

        var alias = post.TemplateAppId;
        var files = post.UnitFiles ?? ShippedUnits.Files(alias);
        if (files.Count == 0) { await Show(State with { Status = $"Post {postId} has no unit files to install into Creator." }); return; }

        try
        {
            await Get<ISynapseStudio>("ino/synapses").DeployFiles(alias, files, new Dictionary<string, string>(StringComparer.Ordinal));
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
        var updated = await Get<IProfile>("profile").ForkPost(postId, newId, request.RemixTitle);
        await Show(State.ApplyProfile(updated, $"Forked and remixed app-post {postId} as {newId}."));
    }

    [On]
    public Task FilterScope(FriendsFilterScope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetScope = string.IsNullOrWhiteSpace(request.Scope) ? "all" : request.Scope;
        return ApplyDecisionAsync(State.SelectScope(targetScope));
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
        if (string.IsNullOrWhiteSpace(alias)) { return ("", null); }
        try
        {
            var studio = Get<ISynapseStudio>("ino/synapses");
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
        => IntoChat.OS.Primitives.AppRef.TryOf(brain, appId, out var appRef) ? appRef.Key : $"apps/{appId}";

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
        var friends = state.Friends ?? [];
        var allPosts = state.Posts ?? [];
        var visiblePosts = state.SelectedScope is "all" or ""
            ? allPosts
            : allPosts.Where(p => string.Equals(p.Scope, state.SelectedScope, StringComparison.OrdinalIgnoreCase)).ToArray();

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
                StatusPill(StatusKind.Done, $"{friends.Length} friends"),
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
                StatCard("kpi-friends-count", "Friends", $"{friends.Count(f => f.Confirmed)}", $"{friends.Count(f => !f.Confirmed)} not confirmed", BadgeTone.Done, "users"),
                StatCard("kpi-friends-embedded", "Embedded Apps", $"{allPosts.Sum(p => (p.EmbeddedApps ?? []).Length)}", $"{(state.PinnedApps ?? []).Length} Pinned", BadgeTone.Accent, "cpu")
            ],
            showRoster ? RenderFriends(state, friends) : null,
            showShared ? RenderSharedByMe(state) : null,
            showShared ? RenderSharedWithMe(state) : null,
            showFeed
                ? (visiblePosts.Length == 0
                    ? Empty("No App-Posts in this scope", "Publish an interactive App-Post or switch feed scope.")
                    : VStack(Space.Md, [.. visiblePosts.Select(post => RenderPostCard(state, post))]).ToNode())
                : null
        ];
    }

    // Friends, how to add one, and the path to share. Sharing is offered only for a confirmed friend.
    private Node RenderFriends(FriendsAppState state, ProfileFriend[] friends)
    {
        var rows = friends.Select(friend => (Node)HStack(Space.Sm,
        [
            new TagPill($"@{friend.Handle} · {friend.DisplayName}", friend.Confirmed ? "accent" : "muted", "users"),
            .. friend.Confirmed
                ? new Node[]
                {
                    Button("Share as viewer", "eye").Key($"share-viewer:{friend.Account}").Small().OnClick(() => ShareTypedPathAsync(friend.Account, "viewer")),
                    Button("Share as editor", "pencil").Key($"share-editor:{friend.Account}").Small().OnClick(() => ShareTypedPathAsync(friend.Account, "editor")),
                    GhostButton("Remove", "user-minus").Key($"unfriend:{friend.Account}").Small().OnClick(() => UnfriendAsync(friend.Account)),
                }
                : [MutedText("Not confirmed. Exchange a friend code to share with them.")],
        ]));

        return Card("Friends", "A friend is confirmed with a code: one of you makes it, the other adds it.")[
            VStack(Space.Sm,
            [
                .. rows,
                HStack(Space.Sm)[
                    Button("Make a friend code", "key").Key("make-code").Small().OnClick(() => MakeCodeAsync()),
                    Text(state.FriendCode)
                ],
                TextField(FieldKey(CodeField), "ada.XXXX-XXXX-XXXX").Key(CodeField).Label("Friend code you received"),
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

    private Node RenderPostCard(FriendsAppState state, ProfilePostItem post)
    {
        var embedded = post.EmbeddedApps ?? [];
        RecordFieldNode[] fields =
        [
            new("Author", $"@{state.Handle}"),
            new("Scope", post.Scope),
            new("App Template", post.TemplateAppId),
            new("Summary", post.Content),
            .. !string.IsNullOrWhiteSpace(post.ForkedFrom) ? [new RecordFieldNode("Forked From", post.ForkedFrom)] : Array.Empty<RecordFieldNode>(),
            .. embedded.Select(child => new RecordFieldNode($"Embedded [{child.Slot}]", $"{child.Title} ({child.TargetNeuronUri})")),
        ];

        return Card(string.IsNullOrWhiteSpace(post.Title) ? $"App-Post · {post.Id}" : post.Title, $"@{state.Handle} · [{post.Scope}] · {post.TemplateAppId}")[
            VStack(Space.Sm,
            [
                Text(post.Content),
                new RecordCardNode(
                    $"App-Post · {post.Scope}",
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
