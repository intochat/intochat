/*
# Friends
Role: Default
Shipped: true
Operations: open, publish, update-bio, add-friend, remove-friend, fork-post, filter-scope

Personal social profile, direct Friends graph, and interactive App-Post feed.
Each Post is an App<TState> template that renders inline in the profile/feed, can embed child apps via NeuronRef slots, and can be forked/remixed into the viewer's brain.
*/
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Apps.Friends
{
    [Signal("friends.open")]
    public sealed record FriendsOpen(string? Scope = null) : Signal;

    [Signal("friends.publish")]
    public sealed record FriendsPublish(
        string? Content = null,
        string Scope = "public",
        string? Id = null,
        string? Title = null,
        string? TemplateAppId = null,
        string? EmbeddedAppIds = null) : Signal;

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
    }

    [Neuron("app.friends")]
    public sealed class FriendsApp : Screen<FriendsAppState>
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

        [On]
        public async Task Open(FriendsOpen request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var profile = await Get<IProfile>("profile").Read();
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? State.SelectedScope : request.Scope.Trim().ToLowerInvariant();
            await ApplyProfileAsync(profile, $"@{profile.Handle} · {(profile.Friends ?? []).Length} friends · {profile.Posts.Length} app-posts", scope);
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

            var profile = Get<IProfile>("profile");
            var updated = await profile.PublishAppPost(new ProfilePostItem(
                Id: id,
                Content: content,
                CreatedAt: DateTimeOffset.UtcNow,
                Circle: scope,
                Title: request.Title ?? "",
                TemplateAppId: templateAppId,
                TemplateSource: unitSource,
                UnitFiles: unitFiles,
                EmbeddedApps: embedded));

            await ApplyProfileAsync(updated, $"Published app-post {id} ({scope}) with {embedded.Length} embedded app(s).");
        }

        [On]
        public async Task UpdateBio(FriendsUpdateBio request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var handle = string.IsNullOrWhiteSpace(request.Handle) ? State.Handle : request.Handle.Trim();
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? State.DisplayName : request.DisplayName.Trim();
            var bio = request.Bio?.Trim() ?? string.Empty;
            var pinned = request.PinnedApps ?? State.PinnedApps ?? [];

            var profile = Get<IProfile>("profile");
            var updated = await profile.UpdateProfile(handle, displayName, bio, pinned);
            await ApplyProfileAsync(updated, $"Updated personal bio for @{handle}.");
        }

        [On]
        public async Task AddFriend(FriendsAddFriend request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var brainId = string.IsNullOrWhiteSpace(request.FriendBrainId) ? "ada" : request.FriendBrainId.Trim();
            var handle = string.IsNullOrWhiteSpace(request.Handle) ? brainId : request.Handle.Trim();
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? handle : request.DisplayName.Trim();
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? "friends" : request.Scope.Trim().ToLowerInvariant();

            var profile = Get<IProfile>("profile");
            var updated = await profile.AddFriend(brainId, handle, displayName, scope);
            await ApplyProfileAsync(updated, $"Added friend @{handle} to direct network ({scope}).");
        }

        [On]
        public async Task RemoveFriend(FriendsRemoveFriend request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var brainId = string.IsNullOrWhiteSpace(request.FriendBrainId) ? "ada" : request.FriendBrainId.Trim();

            var profile = Get<IProfile>("profile");
            var updated = await profile.RemoveFriend(brainId);
            await ApplyProfileAsync(updated, $"Removed direct friend {brainId}.");
        }

        [On]
        public async Task ForkApp(FriendsForkApp request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var postId = string.IsNullOrWhiteSpace(request.PostId) ? "post-1" : request.PostId.Trim();
            var profile = await Get<IProfile>("profile").Read();
            var post = profile.Posts.FirstOrDefault(p => string.Equals(p.Id, postId, StringComparison.OrdinalIgnoreCase));
            if (post is null)
            {
                await Show(State with { Status = $"Post {postId} not found to fork." });
                return;
            }

            var alias = post.TemplateAppId;
            var files = post.UnitFiles ?? ShippedUnits.Files(alias);
            if (files.Count == 0)
            {
                await Show(State with { Status = $"Post {postId} has no unit files to install into Creator." });
                return;
            }

            try
            {
                var studio = Get<ISynapseStudio>("ino/synapses");
                await studio.DeployFiles(alias, files, new Dictionary<string, string>(StringComparer.Ordinal));
                await Show(State with { Status = $"Forked app-post template {alias} into Creator; ready for consent." });
            }
            catch (Exception error)
            {
                await Show(State with { Status = $"Could not fork app {alias}: {error.Message}" });
            }
        }

        [On]
        public async Task ForkPost(FriendsForkPost request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var postId = string.IsNullOrWhiteSpace(request.PostId) ? "post-1" : request.PostId.Trim();
            var newId = string.IsNullOrWhiteSpace(request.NewPostId) ? $"post-fork-{Guid.NewGuid():N}" : request.NewPostId.Trim();

            var profile = Get<IProfile>("profile");
            var updated = await profile.ForkPost(postId, newId, request.RemixTitle);
            await ApplyProfileAsync(updated, $"Forked and remixed app-post {postId} as {newId}.");
        }

        [On]
        public Task FilterScope(FriendsFilterScope request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? "all" : request.Scope.Trim().ToLowerInvariant();
            return Show(State with { SelectedScope = scope, Status = $"Filtered feed to {scope} posts." });
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
            catch (Exception ex)
            {
                NullLogger.Instance.LogSwallowed(ex, "Failed to read unit template for alias.");
                return ("", null);
            }
        }

        private Task ApplyProfileAsync(ProfileState profile, string status, string? selectedScope = null)
            => Show(new FriendsAppState(
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

        public new FriendsAppState State => base.State;
        public View Render() => Render(State);

        private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
        {
            ArgumentNullException.ThrowIfNull(signal);
            var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
            return new Emit("app.friends", alias, JsonSerializer.SerializeToElement(signal));
        }

        protected override View Render(FriendsAppState state)
        {
            var friends = state.Friends ?? [];
            var allPosts = state.Posts ?? [];
            var visiblePosts = state.SelectedScope is "all" or ""
                ? allPosts
                : allPosts.Where(p => string.Equals(p.Scope, state.SelectedScope, StringComparison.OrdinalIgnoreCase)).ToArray();

            var items = new List<Node>
            {
                new Grid(
                    3,
                    [
                        Ui.StatCard("kpi-friends-posts", "App-Posts", $"{allPosts.Length}", $"@{state.Handle}", BadgeTone.Accent, "layout-grid"),
                        Ui.StatCard("kpi-friends-count", "Direct Friends", $"{friends.Length}", "Personal Graph", BadgeTone.Done, "users"),
                        Ui.StatCard("kpi-friends-embedded", "Embedded Apps", $"{allPosts.Sum(p => (p.EmbeddedApps ?? []).Length)}", $"{state.PinnedApps.Length} Pinned", BadgeTone.Accent, "cpu"),
                    ],
                    Gap: 10),
                new Surface(
                    [
                        new Heading($"{state.DisplayName} (@{state.Handle})", Level: 2, Subtitle: state.Bio),
                        new Row(
                        [
                            new TagPill($"@{state.Handle}", "accent", "user"),
                            new TagPill($"{friends.Length} friend(s)", "ok", "users"),
                            new TagPill($"{allPosts.Length} app-post(s)", "neutral", "layout-grid"),
                            .. state.PinnedApps.Select(app => (Node)new TagPill(app, "ok", "pin")),
                        ]) { Gap = 6 },
                        new Text($"@{state.Handle} — {state.Bio}"),
                        new Text(state.Status) { Tone = "muted" },
                        new Row(
                        [
                            new Button("publish-app-post", "Publish App-Post", Emit: Emits(new FriendsPublish("Interactive revenue & sketch board", "friends")), Tone: ButtonTone.Accent) { Icon = "plus", Small = true },
                            new Button("add-friend", "Add Friend", Emit: Emits(new FriendsAddFriend("ada", "ada", "Ada Lovelace", "friends")), Tone: ButtonTone.Neutral) { Icon = "user-plus", Small = true },
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
                items.Add(RenderPostSurface(state, post));
            }

            return new View("Friends", new Column([.. items]) { Gap = 12 });
        }

        private Node RenderPostSurface(FriendsAppState state, ProfilePostItem post)
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

            Node[] postChildren =
            [
                new Heading(string.IsNullOrWhiteSpace(post.Title) ? post.Id : post.Title, Level: 3, Subtitle: $"App Template `{post.TemplateAppId}` · [{post.Scope}]"),
                new Row(
                [
                    new TagPill($"@{state.Handle}", "accent", "user"),
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
                    Open: new Button($"fork-{post.Id}", "Fork App-Post", Emit: Emits(new FriendsForkPost(post.Id)))),
                .. embedded.Select(child => (Node)new NeuronRef("viewport", child.TargetNeuronUri)),
                new Row(
                [
                    new Button($"fork-and-install:{post.Id}", "Fork & Install", Emit: Emits(new FriendsForkApp(post.Id)), Tone: ButtonTone.Accent) { Icon = "download", Small = true },
                    new Button($"fork-app-template:{post.Id}", "Fork App Template", Emit: Emits(new FriendsForkPost(post.Id)), Tone: ButtonTone.Neutral) { Icon = "git-branch", Small = true },
                    new Button($"open-in-creator:{post.Id}", "Open in Creator", Intent: AppSignals.OpenApp("creator")) { Icon = "wand-sparkles", Small = true },
                ]) { Gap = 8 },
            ];

            return new Surface(postChildren, Kind: SurfaceKind.Elevated, Title: $"App-Post · {post.Id}", Subtitle: post.TemplateAppId);
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
