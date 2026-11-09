/*
# Friends
Role: Default
Shipped: true
Operations: open, publish, update-bio, add-friend, remove-friend, fork-post, filter-scope

Personal social profile, direct Friends graph, and interactive App-Post feed.
Each Post is an App<TState> template that renders inline in the profile/feed, can embed child apps via NeuronRef slots, and can be forked/remixed into the viewer's brain.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Friends
{
[DigitalBrain.Signal("friends.open")]
public sealed record FriendsOpen(string? Scope = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.publish")]
public sealed record FriendsPublish(
    string? Content = null,
    string Scope = "public",
    string? Id = null,
    string? Title = null,
    string? TemplateAppId = null,
    string? EmbeddedAppIds = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.update-bio")]
public sealed record FriendsUpdateBio(
    string Handle = "user",
    string Bio = "",
    string[]? PinnedApps = null,
    string DisplayName = "") : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.add-friend")]
public sealed record FriendsAddFriend(
    string FriendBrainId = "ada",
    string Handle = "ada",
    string DisplayName = "Ada Lovelace",
    string Scope = "friends") : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.remove-friend")]
public sealed record FriendsRemoveFriend(
    string FriendBrainId = "ada") : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.fork-post")]
public sealed record FriendsForkPost(
    string PostId = "post-1",
    string? NewPostId = null,
    string? RemixTitle = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("friends.filter-scope")]
public sealed record FriendsFilterScope(
    string Scope = "all") : DigitalBrain.Signal;

public sealed record FriendsAppState(
    string Handle,
    string DisplayName,
    string Bio,
    string[] PinnedApps,
    ProfileFriend[] Friends,
    ProfilePostItem[] Posts,
    string SelectedScope,
    string Status)
{
    public FriendsAppState() : this("user", "User", "", [], [], [], "all", "Ready") { }
}

[DigitalBrain.App("friends", Accent = "#FB7185", Category = "Social")]
public sealed class FriendsApp : DigitalBrain.App<FriendsAppState>
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(FriendsApp));

    public static AppBundle Bundle { get; } = new(
        "friends",
        "2.0.0",
        new AppManifest(
            Title: "Friends",
            Description: "Personal social profile, direct friends graph, and interactive App-Post templates that can embed other apps.",
            Operations:
            [
                new("open", "Show personal profile, friends list, and interactive app-post feed."),
                new("publish", "Publish an interactive App-Post template with optional embedded child apps."),
                new("update-bio", "Update personal profile handle, display name, bio, and pinned apps."),
                new("add-friend", "Add a direct friend by personal brain ID and handle."),
                new("remove-friend", "Remove a direct friend from your personal profile."),
                new("fork-post", "Fork an interactive App-Post template (and its embedded app slots) into a new post."),
                new("filter-scope", "Filter the feed by scope (all, public, friends)."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(FriendsApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public async Task Open(FriendsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await Get<IProfile>("profile").Read();
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? State.SelectedScope : request.Scope.Trim().ToLowerInvariant();
        ApplyProfile(profile, $"@{profile.Handle} · {(profile.Friends ?? []).Length} friends · {profile.Posts.Length} app-posts", scope);
    }

    [DigitalBrain.On]
    public async Task Publish(FriendsPublish request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            Set(State with { Status = "Post content cannot be empty." });
            return;
        }

        var postId = string.IsNullOrWhiteSpace(request.Id)
            ? $"post-{State.Posts.Length + 1}"
            : request.Id.Trim();
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "public" : request.Scope.Trim().ToLowerInvariant();
        var title = string.IsNullOrWhiteSpace(request.Title) ? request.Content.Trim() : request.Title.Trim();
        var templateAppId = string.IsNullOrWhiteSpace(request.TemplateAppId)
            ? $"friends.post.{postId}"
            : request.TemplateAppId.Trim();

        var embeddedApps = ParseEmbeddedApps(request.EmbeddedAppIds);
        var templateSource = BuildPostAppTemplateSource(postId, title, embeddedApps);

        var post = new ProfilePostItem(
            Id: postId,
            Content: request.Content.Trim(),
            CreatedAt: DateTimeOffset.UtcNow,
            Circle: scope,
            Title: title,
            TemplateAppId: templateAppId,
            TemplateSource: templateSource,
            EmbeddedApps: embeddedApps);

        var profile = await Get<IProfile>("profile").PublishAppPost(post);
        ApplyProfile(profile, $"Published App-Post {postId} [{scope}] ({embeddedApps.Length} embedded app(s))");
    }

    [DigitalBrain.On]
    public async Task UpdateBio(FriendsUpdateBio request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await Get<IProfile>("profile").UpdateProfile(
            request.Handle,
            request.DisplayName,
            request.Bio,
            request.PinnedApps ?? State.PinnedApps);
        ApplyProfile(profile, $"Updated profile @{profile.Handle}");
    }

    [DigitalBrain.On]
    public async Task AddFriend(FriendsAddFriend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FriendBrainId))
        {
            Set(State with { Status = "Friend brain ID cannot be empty." });
            return;
        }

        var profile = await Get<IProfile>("profile").AddFriend(
            request.FriendBrainId,
            request.Handle,
            request.DisplayName,
            request.Scope);
        ApplyProfile(profile, $"Added friend @{request.Handle.Trim().TrimStart('@')} ({request.FriendBrainId.Trim()})");
    }

    [DigitalBrain.On]
    public async Task RemoveFriend(FriendsRemoveFriend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FriendBrainId))
        {
            Set(State with { Status = "Friend brain ID cannot be empty." });
            return;
        }

        var profile = await Get<IProfile>("profile").RemoveFriend(request.FriendBrainId);
        ApplyProfile(profile, $"Removed friend {request.FriendBrainId.Trim()}");
    }

    [DigitalBrain.On]
    public async Task ForkPost(FriendsForkPost request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceId = string.IsNullOrWhiteSpace(request.PostId) ? "post-1" : request.PostId.Trim();
        var newId = string.IsNullOrWhiteSpace(request.NewPostId)
            ? $"{sourceId}-fork-{State.Posts.Length + 1}"
            : request.NewPostId.Trim();

        var profile = await Get<IProfile>("profile").ForkPost(sourceId, newId, request.RemixTitle);
        var forkedPost = profile.Posts.FirstOrDefault(p => string.Equals(p.Id, newId, StringComparison.Ordinal));
        var promoted = forkedPost is not null && await TryPromoteForkedTemplateAsync(forkedPost);
        ApplyProfile(
            profile,
            promoted
                ? $"Forked App-Post {sourceId} -> {newId} & staged in Creator ({forkedPost!.TemplateAppId})"
                : $"Forked App-Post {sourceId} -> {newId}");
    }

    private async Task<bool> TryPromoteForkedTemplateAsync(ProfilePostItem forkedPost)
    {
        try
        {
            var session = Get<Creator.ICreatorSession>("session");
            var source = string.IsNullOrWhiteSpace(forkedPost.TemplateSource)
                ? BuildPostAppTemplateSource(forkedPost.Id, forkedPost.Title, forkedPost.EmbeddedApps ?? [])
                : forkedPost.TemplateSource;
            var (compiled, diagnostics) = Creator.CreatorSession.CompileInMemory(source);
            await session.RecordPrompt(
                forkedPost.TemplateAppId,
                $"Remix App-Post template '{forkedPost.Title}' ({forkedPost.Id})",
                source,
                compiled,
                diagnostics,
                "en");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "CreatorSession neuron is optional when running FriendsApp standalone.");
            return false;
        }
    }

    [DigitalBrain.On]
    public void FilterScope(FriendsFilterScope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "all" : request.Scope.Trim().ToLowerInvariant();
        Set(State with
        {
            SelectedScope = scope,
            Status = $"Filtered feed: {scope}",
        });
    }

    private void ApplyProfile(ProfileState profile, string status, string? selectedScope = null)
        => Set(new FriendsAppState(
            profile.Handle,
            string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Handle : profile.DisplayName,
            profile.Bio,
            profile.PinnedApps,
            profile.Friends ?? [],
            profile.Posts,
            selectedScope ?? State.SelectedScope,
            status));

    private static PostEmbeddedApp[] ParseEmbeddedApps(string? rawEmbeddedApps)
    {
        if (string.IsNullOrWhiteSpace(rawEmbeddedApps))
        {
            return
            [
                new("canvas", "markdraw", "Markdraw Canvas", "neuron://local/apps/markdraw"),
                new("analytics", "data", "Data Studio", "neuron://local/apps/data"),
            ];
        }

        var ids = rawEmbeddedApps
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ids.Select((appId, idx) =>
        {
            var clean = appId.ToLowerInvariant();
            var title = char.ToUpperInvariant(clean[0]) + clean[1..];
            return new PostEmbeddedApp(
                Slot: $"slot-{idx + 1}",
                AppId: clean,
                Title: $"{title} App",
                TargetNeuronUri: $"neuron://local/apps/{clean}");
        }).ToArray();
    }

    private static string BuildPostAppTemplateSource(string postId, string title, PostEmbeddedApp[] embeddedApps)
    {
        var slotsComment = embeddedApps.Length == 0
            ? "// Standalone App-Post template"
            : $"// Embedded child apps: {string.Join(", ", embeddedApps.Select(e => $"{e.Slot}->{e.TargetNeuronUri}"))}";
        return $$"""
            {{slotsComment}}
            [DigitalBrain.App("post.{{postId}}")]
            public sealed class PostTemplateApp : DigitalBrain.App<PostTemplateState>
            {
                public View Render() => new("{{title.Replace("\"", "'")}}", new Column([
                    {{string.Join(",\n        ", embeddedApps.Select(e => $"new NeuronRef(\"viewport\", \"{e.TargetNeuronUri}\")"))}}
                ]));
            }
            """;
    }

    public View RenderSide()
    {
        var friends = State.Friends ?? [];
        var posts = State.Posts ?? [];
        var pinned = State.PinnedApps ?? [];

        return new(
            "Profile & Friends",
            new Column(
            [
                Ui.StatCard("friends-handle-stat", $"@{State.Handle}", $"{posts.Length}", "App-Posts", BadgeTone.Accent, "users"),
                new Divider("FEED SCOPES"),
                Ui.NavItem("scope-all", "All Posts", "sparkles", "filter-scope:all", selected: State.SelectedScope == "all", badge: $"{posts.Length}"),
                Ui.NavItem("scope-public", "Public Profile", "globe", "filter-scope:public", selected: State.SelectedScope == "public", badge: $"{posts.Count(p => p.Scope == "public")}"),
                Ui.NavItem("scope-friends", "Friends Only", "users", "filter-scope:friends", selected: State.SelectedScope == "friends", badge: $"{posts.Count(p => p.Scope == "friends")}"),
                new Divider($"DIRECT FRIENDS ({friends.Length})"),
                .. friends.Select(f => (Node)Ui.NavItem(
                    $"friend-{f.BrainId}",
                    $"@{f.Handle} ({f.DisplayName})",
                    "user",
                    $"filter-scope:{f.Scope}",
                    selected: false,
                    badge: f.Scope)),
                new Divider("PINNED APPS"),
                .. pinned.Select(app => (Node)Ui.NavItem($"pin-{app}", app, $"app:{app}", "open", selected: false, badge: "Pinned")),
            ])
            { Gap = 6 });
    }

    public View Render()
    {
        var friends = State.Friends ?? [];
        var allPosts = State.Posts ?? [];
        var visiblePosts = State.SelectedScope is "all" or ""
            ? allPosts
            : allPosts.Where(p => string.Equals(p.Scope, State.SelectedScope, StringComparison.OrdinalIgnoreCase)).ToArray();

        var totalEmbedded = allPosts.Sum(p => (p.EmbeddedApps ?? []).Length);

        var items = new List<Node>
        {
            new Grid(
                3,
                [
                    Ui.StatCard("kpi-friends-posts", "App-Posts", $"{allPosts.Length}", $"@{State.Handle}", BadgeTone.Accent, "layout-grid"),
                    Ui.StatCard("kpi-friends-count", "Direct Friends", $"{friends.Length}", "Personal Graph", BadgeTone.Done, "users"),
                    Ui.StatCard("kpi-friends-embedded", "Embedded Apps", $"{totalEmbedded}", $"{State.PinnedApps.Length} Pinned", BadgeTone.Accent, "cpu"),
                ],
                Gap: 10),
            new Surface(
                [
                    new Heading($"{State.DisplayName} (@{State.Handle})", Level: 2, Subtitle: State.Bio),
                    new Row(
                    [
                        new TagPill($"@{State.Handle}", "accent", "user"),
                        new TagPill($"{friends.Length} friend(s)", "ok", "users"),
                        new TagPill($"{allPosts.Length} app-post(s)", "neutral", "layout-grid"),
                        .. State.PinnedApps.Select(app => (Node)new TagPill(app, "ok", "pin")),
                    ]) { Gap = 6 },
                    new Text($"@{State.Handle} — {State.Bio}"),
                    new Text(State.Status) { Tone = "muted" },
                    new Row(
                    [
                        new Button("Publish App-Post", new ButtonAction("friends", "publish", ["Interactive revenue & sketch board", "friends"]), Tone: ButtonTone.Accent)
                        {
                            Icon = "plus",
                            Small = true,
                        },
                        new Button("Add Friend @ada", new ButtonAction("friends", "add-friend", ["ada", "ada", "Ada Lovelace", "friends"]))
                        {
                            Icon = "user-plus",
                            Small = true,
                        },
                    ]) { Gap = 8 },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "Personal Profile & Friends Network"),
        };

        if (friends.Length > 0)
        {
            items.Add(new Surface(
                [
                    new Heading("Direct Friends", Level: 3, Subtitle: "Personal brains connected directly without intermediate Circle brains."),
                    new Row(
                        friends.Select(f => (Node)new TagPill($"@{f.Handle} · {f.DisplayName} ({f.Scope})", "accent", "users")).ToArray())
                    { Gap = 6 },
                ],
                Kind: SurfaceKind.Elevated,
                Title: "Friends Graph"));
        }

        foreach (var post in visiblePosts)
        {
            var embedded = post.EmbeddedApps ?? [];
            var fields = new List<RecordFieldNode>
            {
                new("Author", $"@{State.Handle}"),
                new("Scope", post.Scope),
                new("App Template", post.TemplateAppId),
                new("Summary", post.Content),
            };
            if (!string.IsNullOrWhiteSpace(post.ForkedFrom))
            {
                fields.Add(new("Forked From", post.ForkedFrom));
            }
            foreach (var child in embedded)
            {
                fields.Add(new($"Embedded [{child.Slot}]", $"{child.Title} ({child.TargetNeuronUri})"));
            }

            var postChildren = new List<Node>
            {
                new Heading(string.IsNullOrWhiteSpace(post.Title) ? post.Id : post.Title, Level: 3, Subtitle: $"App Template `{post.TemplateAppId}` · [{post.Scope}]"),
                new Row(
                [
                    new TagPill($"@{State.Handle}", "accent", "user"),
                    new TagPill(post.Scope, "neutral", "users"),
                    new TagPill(post.TemplateAppId, "ok", "cpu"),
                    .. !string.IsNullOrWhiteSpace(post.ForkedFrom)
                        ? new Node[] { new TagPill($"Forked from {post.ForkedFrom}", "accent", "git-branch") }
                        : [],
                ]) { Gap = 6 },
                new Text($"[{post.Scope}] {post.Id}: {post.Content}"),
                new RecordCardNode(
                    $"App-Post · {post.Scope}",
                    post.Id,
                    fields,
                    Subtitle: post.Content,
                    LinkLabel: "Fork App-Post",
                    OnOpen: new ButtonAction("friends", "fork-post", [post.Id])),
            };

            foreach (var child in embedded)
            {
                postChildren.Add(new NeuronRef("viewport", child.TargetNeuronUri));
            }

            postChildren.Add(new Row(
            [
                new Button("Fork App Template", new ButtonAction("friends", "fork-post", [post.Id]), Tone: ButtonTone.Accent)
                {
                    Icon = "git-branch",
                    Small = true,
                },
                new Button("Open in Creator", new ButtonAction("creator", "prompt", [$"Remix App-Post '{post.Title}'", post.TemplateAppId]))
                {
                    Icon = "wand-sparkles",
                    Small = true,
                },
            ]) { Gap = 8 });

            items.Add(new Surface(
                postChildren,
                Kind: SurfaceKind.Elevated,
                Title: $"App-Post · {post.Id}",
                Subtitle: post.TemplateAppId));
        }

        return new View("Friends", new Column(items.ToArray()) { Gap = 12 });
    }

    [DigitalBrain.Scenario("Personal profile connects friends, publishes an App-Post with embedded apps, and forks the template")]
    public async Task VerifyFriendsProfileAndAppPostsScenario()
    {
        await UpdateBio(new FriendsUpdateBio("vlad", "Building AI-native DigitalBrain social apps", ["creator", "friends"], "Vlad Horb"));
        await AddFriend(new FriendsAddFriend("ada-brain", "ada", "Ada Lovelace", "friends"));
        await Publish(new FriendsPublish(
            Content: "Live Q4 revenue canvas with embedded Markdraw & Data Studio apps!",
            Scope: "friends",
            Id: "post-1",
            Title: "Q4 Revenue & Architecture Board",
            TemplateAppId: "friends.post.q4-board",
            EmbeddedAppIds: "markdraw,data"));
        await ForkPost(new FriendsForkPost("post-1", "post-2", "Remix · Q4 Revenue & Architecture Board"));

        if (State.Handle != "vlad"
            || State.Friends.Length != 1
            || State.Friends[0].Handle != "ada"
            || State.Posts.Length != 2
            || State.Posts[0].ForkedFrom != "@vlad/post-1"
            || (State.Posts[1].EmbeddedApps ?? []).Length != 2)
        {
            throw new InvalidOperationException("Expected FriendsApp to persist personal profile, direct friend, embedded-app post template, and forked post in ProfileNeuron.");
        }
    }
}
}

namespace IntoChat.OS.Apps.Post
{
    [DigitalBrain.Signal("post.open")]
    public sealed record PostOpen : DigitalBrain.Signal;

    [DigitalBrain.Signal("post.publish")]
    public sealed record PostPublish(
        string? Content = null,
        string Circle = "public",
        string? Id = null) : DigitalBrain.Signal;

    [DigitalBrain.Signal("post.update-bio")]
    public sealed record PostUpdateBio(
        string Handle = "user",
        string Bio = "",
        string[]? PinnedApps = null) : DigitalBrain.Signal;
}
