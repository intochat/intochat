/*
# Friends
Role: Default
Shipped: true
Operations: open, publish, update-bio, add-friend, remove-friend, fork-post, filter-scope

Personal social profile, direct Friends graph, and interactive App-Post feed.
Each Post is an App<TState> template that renders inline in the profile/feed, can embed child apps via NeuronRef slots, and can be forked/remixed into the viewer's brain.
*/
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Friends
{
    [Signal("friends.open")]
    public sealed record FriendsOpen(string? Scope = null) : Signal;

    [Signal("friends.publish")]
    public sealed record FriendsPublish(string? Content = null, string Scope = "public", string? Id = null, string? Title = null, string? TemplateAppId = null, string? EmbeddedAppIds = null) : Signal;

    [Signal("friends.update-bio")]
    public sealed record FriendsUpdateBio(string Handle = "user", string Bio = "", string[]? PinnedApps = null, string DisplayName = "") : Signal;

    [Signal("friends.add-friend")]
    public sealed record FriendsAddFriend(string FriendBrainId = "ada", string Handle = "ada", string DisplayName = "Ada Lovelace", string Scope = "friends") : Signal;

    [Signal("friends.remove-friend")]
    public sealed record FriendsRemoveFriend(string FriendBrainId = "ada") : Signal;

    [Signal("friends.fork_app")]
    public sealed record FriendsForkApp(string PostId = "post-1") : Signal;

    [Signal("friends.fork-post")]
    public sealed record FriendsForkPost(string PostId = "post-1", string? NewPostId = null, string? RemixTitle = null) : Signal;

    [Signal("friends.filter-scope")]
    public sealed record FriendsFilterScope(string Scope = "all") : Signal;

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

        [Invariant("SelectedScope is all, public, or friends."), JsonIgnore]
        public bool ValidScope => SelectedScope is "all" or "public" or "friends";

        [Invariant("Profile handle is never empty."), JsonIgnore]
        public bool HandlePresent => !string.IsNullOrWhiteSpace(Handle);

        public Rule SupportedScope(string? scope) =>
            Decision.Rule("invalid_scope", string.IsNullOrWhiteSpace(scope) || scope.Trim().ToLowerInvariant() is not ("all" or "public" or "friends"), "Feed scope must be all, public, or friends.");

        public Decision<FriendsAppState> SelectScope([Sample("all", "public", "friends", "secret")] string scope)
        {
            if (Decision.Check("filter-scope", SupportedScope(scope)) is { } refusal) { return refusal; }
            var normalized = scope.Trim().ToLowerInvariant();
            return Decision.Accept(this with { SelectedScope = normalized, Status = $"Filtered feed to {normalized} posts." });
        }

        public static string NormalizeScope(string? scope, string fallback = "all") => scope?.Trim().ToLowerInvariant() switch
        {
            "public" => "public",
            "friends" => "friends",
            "all" => "all",
            _ => fallback is "public" or "friends" ? fallback : "all",
        };

        [IgnoreExplore]
        public FriendsAppState ApplyProfile(ProfileState profile, string status, string? selectedScope = null) => new(
            string.IsNullOrWhiteSpace(profile.Handle) ? "user" : profile.Handle,
            string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Handle : profile.DisplayName,
            profile.Bio ?? "",
            profile.PinnedApps ?? [],
            profile.Friends ?? [],
            profile.Posts ?? [],
            NormalizeScope(selectedScope, SelectedScope),
            status);
    }

    [Neuron("app.friends"), Screen("app.friends", Title = "Friends")]
    public sealed class FriendsApp : AggregateScreen<FriendsAppState>
    {
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

        public new FriendsAppState State => base.State;
        public View Render() => PreviewView(State);

        [On]
        public async Task Open(FriendsOpen request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var profile = await Get<IProfile>("profile").Read();
            var scope = FriendsAppState.NormalizeScope(request.Scope, State.SelectedScope);
            await Show(State.ApplyProfile(profile, $"@{profile.Handle} · {(profile.Friends ?? []).Length} friends · {profile.Posts.Length} app-posts", scope));
        }

        [On]
        public async Task Publish(FriendsPublish request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var content = string.IsNullOrWhiteSpace(request.Content) ? "New App-Post" : request.Content.Trim();
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? "public" : request.Scope.Trim().ToLowerInvariant();
            var id = string.IsNullOrWhiteSpace(request.Id) ? $"post-{Guid.NewGuid():N}" : request.Id.Trim();
            var templateAppId = string.IsNullOrWhiteSpace(request.TemplateAppId) ? $"post.app.{id}" : request.TemplateAppId.Trim().ToLowerInvariant();
            var embedded = ParseEmbeddedApps(request.EmbeddedAppIds);
            var (unitSource, unitFiles) = await TryReadUnitTemplateAsync(templateAppId);

            var updated = await Get<IProfile>("profile").PublishAppPost(new ProfilePostItem(
                Id: id,
                Content: content,
                CreatedAt: DateTimeOffset.UtcNow,
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
            var handle = string.IsNullOrWhiteSpace(request.Handle) ? State.Handle : request.Handle.Trim();
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? State.DisplayName : request.DisplayName.Trim();
            var updated = await Get<IProfile>("profile").UpdateProfile(handle, displayName, request.Bio?.Trim() ?? string.Empty, request.PinnedApps ?? State.PinnedApps ?? []);
            await Show(State.ApplyProfile(updated, $"Updated personal bio for @{handle}."));
        }

        [On]
        public async Task AddFriend(FriendsAddFriend request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var brainId = string.IsNullOrWhiteSpace(request.FriendBrainId) ? "ada" : request.FriendBrainId.Trim();
            var handle = string.IsNullOrWhiteSpace(request.Handle) ? brainId : request.Handle.Trim();
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? handle : request.DisplayName.Trim();
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? "friends" : request.Scope.Trim().ToLowerInvariant();
            var updated = await Get<IProfile>("profile").AddFriend(brainId, handle, displayName, scope);
            await Show(State.ApplyProfile(updated, $"Added friend @{handle} to direct network ({scope})."));
        }

        [On]
        public async Task RemoveFriend(FriendsRemoveFriend request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var brainId = string.IsNullOrWhiteSpace(request.FriendBrainId) ? "ada" : request.FriendBrainId.Trim();
            var updated = await Get<IProfile>("profile").RemoveFriend(brainId);
            await Show(State.ApplyProfile(updated, $"Removed direct friend {brainId}."));
        }

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
            return ApplyDecisionAsync(State.SelectScope(FriendsAppState.NormalizeScope(request.Scope, "all")));
        }

        public Task PublishQuickPostAsync() => Publish(new FriendsPublish("Interactive revenue & sketch board", "friends"));
        public Task AddDefaultFriendAsync() => AddFriend(new FriendsAddFriend("ada", "ada", "Ada Lovelace", "friends"));
        public Task ForkAndInstallPostAsync(string postId) => ForkApp(new FriendsForkApp(postId));
        public Task ForkPostTemplateAsync(string postId) => ForkPost(new FriendsForkPost(postId));

        private static string BrainId => "local";

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

        private static PostEmbeddedApp[] ParseEmbeddedApps(string? rawEmbeddedApps) =>
            string.IsNullOrWhiteSpace(rawEmbeddedApps)
                ? [new("canvas", "markdraw", "Markdraw Canvas", $"{BrainId}/apps/markdraw"), new("analytics", "data", "Data Studio", $"{BrainId}/apps/data")]
                : [.. rawEmbeddedApps
                    .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select((appId, idx) =>
                    {
                        var clean = appId.ToLowerInvariant();
                        return new PostEmbeddedApp($"slot-{idx + 1}", clean, $"{char.ToUpperInvariant(clean[0]) + clean[1..]} App", $"{BrainId}/apps/{clean}");
                    })];

        protected override Node Render(FriendsAppState state)
        {
            var friends = state.Friends ?? [];
            var allPosts = state.Posts ?? [];
            var visiblePosts = state.SelectedScope is "all" or ""
                ? allPosts
                : allPosts.Where(p => string.Equals(p.Scope, state.SelectedScope, StringComparison.OrdinalIgnoreCase)).ToArray();

            return VStack(Space.Md)[
                HeaderBar(
                    $"{state.DisplayName} (@{state.Handle})",
                    string.IsNullOrWhiteSpace(state.Bio) ? state.Status : $"{state.Bio} · {state.Status}",
                    StatusPill(StatusKind.Done, $"{friends.Length} friends"),
                    Button("Add Friend", "user-plus").Key("add-friend").Small().OnClick(() => AddDefaultFriendAsync()),
                    PrimaryButton("Publish App-Post", Icons.Plus).Key("publish-app-post").Small().OnClick(() => PublishQuickPostAsync())),
                Tabs(
                    state.SelectedScope,
                    Tab("all", "All Posts", "layout-grid").OnSelect<FriendsAppState>(x => x.SelectScope("all")),
                    Tab("public", "Public", "globe").OnSelect<FriendsAppState>(x => x.SelectScope("public")),
                    Tab("friends", "Friends", "users").OnSelect<FriendsAppState>(x => x.SelectScope("friends"))),
                Grid(3, Space.Sm)[
                    StatCard("kpi-friends-posts", "App-Posts", $"{allPosts.Length}", $"@{state.Handle}", BadgeTone.Accent, "layout-grid"),
                    StatCard("kpi-friends-count", "Direct Friends", $"{friends.Length}", "Personal Graph", BadgeTone.Done, "users"),
                    StatCard("kpi-friends-embedded", "Embedded Apps", $"{allPosts.Sum(p => (p.EmbeddedApps ?? []).Length)}", $"{(state.PinnedApps ?? []).Length} Pinned", BadgeTone.Accent, "cpu")
                ],
                friends.Length > 0
                    ? Card("Direct Friends", "Personal brains connected directly without intermediate Circle brains.")[
                        HStack(Space.Xs, [.. friends.Select(f => (Node)new TagPill($"@{f.Handle} · {f.DisplayName} ({f.Scope})", "accent", "users"))])
                    ].ToNode()
                    : null,
                visiblePosts.Length == 0
                    ? Empty("No App-Posts in this scope", "Publish an interactive App-Post or switch feed scope.")
                    : VStack(Space.Md, [.. visiblePosts.Select(post => RenderPostCard(state, post))]).ToNode()
            ];
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
                    .. embedded.Select(child => (Node)Slot(child.TargetNeuronUri)),
                    HStack(Space.Sm)[
                        Button("Fork & Install", "download").Key($"fork-and-install:{post.Id}").Small().OnClick(() => ForkAndInstallPostAsync(post.Id)),
                        Button("Fork App Template", "git-branch").Key($"fork-app-template:{post.Id}").Small().OnClick(() => ForkPostTemplateAsync(post.Id)),
                        GhostButton("Open in Creator", "wand-sparkles").Key($"open-in-creator:{post.Id}").Small().WithIntent(IntentKind.OpenWindow, "creator")
                    ],
                ])
            ];
        }
    }
}

namespace IntoChat.OS.Apps.Post
{
    [Signal("post.open")]
    public sealed record PostOpen : Signal;

    [Signal("post.publish")]
    public sealed record PostPublish(string? Content = null, string Circle = "public", string? Id = null) : Signal;

    [Signal("post.update-bio")]
    public sealed record PostUpdateBio(string Handle = "user", string Bio = "", string[]? PinnedApps = null) : Signal;
}
