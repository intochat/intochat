/*
# Friends
Role: Default
Shipped: true
Operations: open, publish, update-bio, add-friend, remove-friend, fork-post, filter-scope

Personal social profile, direct Friends graph, and interactive App-Post feed.
Each Post is an App<TState> template that renders inline in the profile/feed, can embed child apps via NeuronRef slots, and can be forked/remixed into the viewer's brain.
*/
using DigitalBrain;
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
    public sealed record FriendsUpdateBio(string Handle = "user", string Bio = "", string[]? PinnedApps = null, string DisplayName = "") : DigitalBrain.Signal;

    [DigitalBrain.Signal("friends.add-friend")]
    public sealed record FriendsAddFriend(string FriendBrainId = "ada", string Handle = "ada", string DisplayName = "Ada Lovelace", string Scope = "friends") : DigitalBrain.Signal;

    [DigitalBrain.Signal("friends.remove-friend")]
    public sealed record FriendsRemoveFriend(string FriendBrainId = "ada") : DigitalBrain.Signal;

    [DigitalBrain.Signal("friends.fork_app")]
    public sealed record FriendsForkApp(string PostId = "post-1") : DigitalBrain.Signal;

    [DigitalBrain.Signal("friends.fork-post")]
    public sealed record FriendsForkPost(string PostId = "post-1", string? NewPostId = null, string? RemixTitle = null) : DigitalBrain.Signal;

    [DigitalBrain.Signal("friends.filter-scope")]
    public sealed record FriendsFilterScope(string Scope = "all") : DigitalBrain.Signal;

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
                    new("fork_app", "Fork and install an App-Post unit template into Creator and submit to IInstalls consent loop."),
                    new("filter-scope", "Filter the feed by scope (all, public, friends)."),
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

            var postId = string.IsNullOrWhiteSpace(request.Id) ? $"post-{State.Posts.Length + 1}" : request.Id.Trim();
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? "public" : request.Scope.Trim().ToLowerInvariant();
            var title = string.IsNullOrWhiteSpace(request.Title) ? request.Content.Trim() : request.Title.Trim();
            var templateAppId = string.IsNullOrWhiteSpace(request.TemplateAppId) ? $"friends.post.{postId}" : request.TemplateAppId.Trim();
            var embeddedApps = ParseEmbeddedApps(request.EmbeddedAppIds);
            var (templateSource, unitFiles) = await TryReadUnitTemplateAsync(templateAppId);

            var post = new ProfilePostItem(
                Id: postId,
                Content: request.Content.Trim(),
                CreatedAt: DateTimeOffset.UtcNow,
                Circle: scope,
                Title: title,
                TemplateAppId: templateAppId,
                TemplateSource: templateSource,
                EmbeddedApps: embeddedApps,
                UnitFiles: unitFiles,
                AuthorBrainId: BrainId);

            var profile = await Get<IProfile>("profile").PublishAppPost(post);
            ApplyProfile(profile, $"Published App-Post {postId} [{scope}] ({embeddedApps.Length} embedded app(s))");
        }

        [DigitalBrain.On]
        public async Task UpdateBio(FriendsUpdateBio request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var profile = await Get<IProfile>("profile").UpdateProfile(request.Handle, request.DisplayName, request.Bio, request.PinnedApps ?? State.PinnedApps);
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

            var profile = await Get<IProfile>("profile").AddFriend(request.FriendBrainId, request.Handle, request.DisplayName, request.Scope);
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
            var newId = string.IsNullOrWhiteSpace(request.NewPostId) ? $"{sourceId}-fork-{State.Posts.Length + 1}" : request.NewPostId.Trim();

            var profile = await Get<IProfile>("profile").ForkPost(sourceId, newId, request.RemixTitle);
            var forkedPost = profile.Posts.FirstOrDefault(p => string.Equals(p.Id, newId, StringComparison.Ordinal));
            if (forkedPost is { UnitFiles.Count: > 0 } && forkedPost.UnitFiles.Keys.Any(k => k.EndsWith(".cs", StringComparison.Ordinal)))
            {
                try
                {
                    await Get<ISynapseStudio>("ino/synapses").DeployFiles(forkedPost.TemplateAppId, forkedPost.UnitFiles, new Dictionary<string, string>(StringComparer.Ordinal));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Swallow(error, "Deploying the forked unit template is optional when the synapse studio is not active.");
                }
            }
            ApplyProfile(profile, $"Forked App-Post {sourceId} -> {newId}");
        }

        [DigitalBrain.On]
        public async Task ForkApp(FriendsForkApp request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var post = State.Posts.FirstOrDefault(p => string.Equals(p.Id, request.PostId, StringComparison.Ordinal));
            if (post is null || post.UnitFiles is null || post.UnitFiles.Count == 0)
            {
                Set(State with { Status = $"Post {request.PostId} contains no installable unit files." });
                return;
            }

            try
            {
                await Get<ISynapseStudio>("ino/synapses").DeployFiles(post.TemplateAppId, post.UnitFiles, new Dictionary<string, string>(StringComparer.Ordinal));
                IInstalls installs;
                try
                {
                    installs = Get<IInstalls>("default");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Swallow(error, "Fallback to synapse.installs when default IInstalls is unavailable.");
                    installs = Get<IInstalls>("synapse.installs");
                }

                var outcome = await installs.Install(new DigitalBrain.InstallRequest(post.TemplateAppId, new Dictionary<string, string>(StringComparer.Ordinal)), CancellationToken.None);
                var status = outcome.Status switch
                {
                    DigitalBrain.InstallStatus.Pending => $"Staged {post.TemplateAppId} for installation ({outcome.Reason}). Awaiting your consent in Inbox.",
                    DigitalBrain.InstallStatus.Installed or DigitalBrain.InstallStatus.Unchanged => $"Installed {post.TemplateAppId} successfully!",
                    _ => $"Install ended with {outcome.Status}: {outcome.Reason}",
                };
                Set(State with { Status = status });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "Installing a forked app requires the synapse studio and installs neurons.");
                Set(State with { Status = $"Could not fork app {post.TemplateAppId}: {error.Message}" });
            }
        }

        [DigitalBrain.On]
        public void FilterScope(FriendsFilterScope request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? "all" : request.Scope.Trim().ToLowerInvariant();
            Set(State with { SelectedScope = scope, Status = $"Filtered feed: {scope}" });
        }

        private string BrainId => "local";

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
                    var shippedSource = string.Join("\n\n", shipped.Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value));
                    return (shippedSource, shipped);
                }
                var files = new Dictionary<string, string>(csFiles, StringComparer.Ordinal);
                foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
                return (string.Join("\n\n", csFiles.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)), files);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "Reading unit files from the synapse studio is optional when publishing a post.");
                return ("", null);
            }
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

        private PostEmbeddedApp[] ParseEmbeddedApps(string? rawEmbeddedApps)
        {
            if (string.IsNullOrWhiteSpace(rawEmbeddedApps))
            {
                return
                [
                    new("canvas", "markdraw", "Markdraw Canvas", $"{BrainId}/apps/markdraw"),
                    new("analytics", "data", "Data Studio", $"{BrainId}/apps/data"),
                ];
            }

            return rawEmbeddedApps
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((appId, idx) =>
                {
                    var clean = appId.ToLowerInvariant();
                    var title = char.ToUpperInvariant(clean[0]) + clean[1..];
                    return new PostEmbeddedApp($"slot-{idx + 1}", clean, $"{title} App", $"{BrainId}/apps/{clean}");
                })
                .ToArray();
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
                    .. friends.Select(f => (Node)Ui.NavItem($"friend-{f.BrainId}", $"@{f.Handle} ({f.DisplayName})", "user", $"filter-scope:{f.Scope}", selected: false, badge: f.Scope)),
                    new Divider("PINNED APPS"),
                    .. pinned.Select(app => (Node)Ui.NavItem($"pin-{app}", app, $"app:{app}", "open", selected: false, badge: "Pinned")),
                ]) { Gap = 6 });
        }

        public View Render()
        {
            var friends = State.Friends ?? [];
            var allPosts = State.Posts ?? [];
            var visiblePosts = State.SelectedScope is "all" or ""
                ? allPosts
                : allPosts.Where(p => string.Equals(p.Scope, State.SelectedScope, StringComparison.OrdinalIgnoreCase)).ToArray();

            var items = new List<Node>
            {
                new Grid(
                    3,
                    [
                        Ui.StatCard("kpi-friends-posts", "App-Posts", $"{allPosts.Length}", $"@{State.Handle}", BadgeTone.Accent, "layout-grid"),
                        Ui.StatCard("kpi-friends-count", "Direct Friends", $"{friends.Length}", "Personal Graph", BadgeTone.Done, "users"),
                        Ui.StatCard("kpi-friends-embedded", "Embedded Apps", $"{allPosts.Sum(p => (p.EmbeddedApps ?? []).Length)}", $"{State.PinnedApps.Length} Pinned", BadgeTone.Accent, "cpu"),
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
                            new Button("publish-app-post", "Publish App-Post", Emit: AppSignals.Emit("friends", "publish", "Interactive revenue & sketch board", "friends"), Tone: ButtonTone.Accent) { Icon = "plus", Small = true },
                            new Button("add-friend", "Add Friend", Emit: AppSignals.Emit("friends", "add-friend", "ada", "ada", "Ada Lovelace", "friends")) { Icon = "user-plus", Small = true },
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
                        new Row(friends.Select(f => (Node)new TagPill($"@{f.Handle} · {f.DisplayName} ({f.Scope})", "accent", "users")).ToArray()) { Gap = 6 },
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Friends Graph"));
            }

            foreach (var post in visiblePosts)
            {
                items.Add(RenderPostSurface(post));
            }

            return new View("Friends", new Column([.. items]) { Gap = 12 });
        }

        private Node RenderPostSurface(ProfilePostItem post)
        {
            var embedded = post.EmbeddedApps ?? [];
            RecordFieldNode[] fields =
            [
                new("Author", $"@{State.Handle}"),
                new("Scope", post.Scope),
                new("App Template", post.TemplateAppId),
                new("Summary", post.Content),
                .. !string.IsNullOrWhiteSpace(post.ForkedFrom) ? [new RecordFieldNode("Forked From", post.ForkedFrom)] : Array.Empty<RecordFieldNode>(),
                .. embedded.Select(child => new RecordFieldNode($"Embedded [{child.Slot}]", $"{child.Title} ({child.TargetNeuronUri})")),
            ];

            Node[] postChildren =
            [
                new Heading(string.IsNullOrWhiteSpace(post.Title) ? post.Id : post.Title, Level: 3, Subtitle: $"App Template `{post.TemplateAppId}` · [{post.Scope}]"),
                new Row(
                [
                    new TagPill($"@{State.Handle}", "accent", "user"),
                    new TagPill(post.Scope, "neutral", "users"),
                    new TagPill(post.TemplateAppId, "ok", "cpu"),
                    .. !string.IsNullOrWhiteSpace(post.ForkedFrom) ? new Node[] { new TagPill($"Forked from {post.ForkedFrom}", "accent", "git-branch") } : [],
                ]) { Gap = 6 },
                new Text($"[{post.Scope}] {post.Id}: {post.Content}"),
                new RecordCardNode(
                    $"App-Post · {post.Scope}",
                    post.Id,
                    fields,
                    Subtitle: post.Content,
                    LinkLabel: "Fork App-Post",
                    Open: new Button($"fork-{post.Id}", "Fork App-Post", Emit: AppSignals.Emit("friends", "fork-post", post.Id))),
                .. embedded.Select(child => (Node)new NeuronRef("viewport", child.TargetNeuronUri)),
                new Row(
                [
                    new Button($"fork-and-install:{post.Id}", "Fork & Install", Emit: AppSignals.Emit("friends", "fork_app", post.Id), Tone: ButtonTone.Accent) { Icon = "download", Small = true },
                    new Button($"fork-app-template:{post.Id}", "Fork App Template", Emit: AppSignals.Emit("friends", "fork-post", post.Id), Tone: ButtonTone.Neutral) { Icon = "git-branch", Small = true },
                    new Button($"open-in-creator:{post.Id}", "Open in Creator", Intent: AppSignals.OpenApp("creator")) { Icon = "wand-sparkles", Small = true },
                ]) { Gap = 8 },
            ];

            return new Surface(postChildren, Kind: SurfaceKind.Elevated, Title: $"App-Post · {post.Id}", Subtitle: post.TemplateAppId);
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
    public sealed record PostPublish(string? Content = null, string Circle = "public", string? Id = null) : DigitalBrain.Signal;

    [DigitalBrain.Signal("post.update-bio")]
    public sealed record PostUpdateBio(string Handle = "user", string Bio = "", string[]? PinnedApps = null) : DigitalBrain.Signal;
}
