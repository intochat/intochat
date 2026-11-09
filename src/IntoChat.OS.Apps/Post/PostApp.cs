/*
# Post
Role: Default
Shipped: true
Operations: open, publish, update-bio

Publishes posts and profile cards to the brain's ProfileNeuron (IProfile) with Circle-scoped visibility ("public", "friends", or a specific Circle id).
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Post;

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

public sealed record PostAppState(
    string Handle,
    string Bio,
    string[] PinnedApps,
    ProfilePostItem[] Posts,
    string Status)
{
    public PostAppState() : this("user", "", [], [], "Ready") { }
}

[DigitalBrain.App("post", Accent = "#FB7185", Category = "Reach")]
public sealed class PostApp : DigitalBrain.App<PostAppState>
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(PostApp));

    public const string AppName = "post";

    public static AppBundle Bundle { get; } = new(
        "post",
        "1.0.0",
        new AppManifest(
            Title: "Post",
            Description: "Public profile and Circle-scoped social post app.",
            Operations:
            [
                new("open", "Show profile and posts."),
                new("publish", "Publish a post to a Circle."),
                new("update-bio", "Update profile handle, bio, and pinned apps."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 9),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(PostApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public async Task Open(PostOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await Get<IProfile>("profile").Read();
        Update(new PostAppState(profile.Handle, profile.Bio, profile.PinnedApps, profile.Posts, $"@{profile.Handle} · {profile.Posts.Length} posts"));
    }

    [DigitalBrain.On]
    public async Task Publish(PostPublish request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            Update(State with { Status = "Post content cannot be empty." });
            return;
        }

        var postId = string.IsNullOrWhiteSpace(request.Id)
            ? $"post-{State.Posts.Length + 1}"
            : request.Id.Trim();
        var circle = string.IsNullOrWhiteSpace(request.Circle) ? "public" : request.Circle.Trim();
        var profile = await Get<IProfile>("profile").PublishPost(postId, request.Content.Trim(), circle);
        Update(new PostAppState(
            profile.Handle,
            profile.Bio,
            profile.PinnedApps,
            profile.Posts,
            $"Published {postId} [{circle}]"));
    }

    [DigitalBrain.On]
    public async Task UpdateBio(PostUpdateBio request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await Get<IProfile>("profile").SetBio(
            request.Handle,
            request.Bio,
            request.PinnedApps ?? State.PinnedApps);
        Update(new PostAppState(
            profile.Handle,
            profile.Bio,
            profile.PinnedApps,
            profile.Posts,
            $"Updated profile @{profile.Handle}"));
    }

    public View RenderSide() => new(
        "Circles & Pinned",
        new Column(
        [
            Ui.StatCard("post-handle-stat", $"@{State.Handle}", $"{State.Posts.Length}", "Published", BadgeTone.Accent, "send"),
            new Divider("CIRCLES"),
            Ui.NavItem("circle-public", "Public Feed", "globe", "open", selected: true, badge: $"{State.Posts.Count(p => p.Circle == "public")}"),
            Ui.NavItem("circle-friends", "Friends Circle", "users", "open", selected: false, badge: $"{State.Posts.Count(p => p.Circle == "friends")}"),
            Ui.NavItem("circle-nordlys", "Nordlys Workspace", "shield-check", "open", selected: false, badge: $"{State.Posts.Count(p => p.Circle != "public" && p.Circle != "friends")}"),
            new Divider("PINNED APPS"),
            .. State.PinnedApps.Select(app => (Node)Ui.NavItem($"pin-{app}", app, $"app:{app}", "open", selected: false, badge: "Pinned")),
        ])
        { Gap = 6 });

    public View Render()
    {
        var items = new List<Node>
        {
            new Grid(
                3,
                [
                    Ui.StatCard("kpi-post-total", "Total Posts", $"{State.Posts.Length}", $"@{State.Handle}", BadgeTone.Accent, "send"),
                    Ui.StatCard("kpi-post-pinned", "Pinned Apps", $"{State.PinnedApps.Length}", "Active", BadgeTone.Done, "pin"),
                    Ui.StatCard("kpi-post-circles", "Active Circles", $"{Math.Max(1, State.Posts.Select(p => p.Circle).Distinct(StringComparer.OrdinalIgnoreCase).Count())}", "Scoped", BadgeTone.Accent, "shield-check"),
                ],
                Gap: 10),
            new Surface(
                [
                    new Heading($"@{State.Handle}", Level: 2, Subtitle: State.Bio),
                    new Row(
                    [
                        new TagPill($"@{State.Handle}", "accent", "user"),
                        new TagPill($"{State.Posts.Length} post(s)", "neutral"),
                        .. State.PinnedApps.Select(app => (Node)new TagPill(app, "ok", "pin")),
                    ]) { Gap = 6 },
                    new Text($"@{State.Handle} — {State.Bio}"),
                    new Text(State.Status) { Tone = "muted" },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "DigitalBrain Profile & Circle Feed"),
        };
        foreach (var post in State.Posts)
        {
            items.Add(new Text($"[{post.Circle}] {post.Id}: {post.Content}"));
            items.Add(new RecordCardNode(
                $"Circle · {post.Circle}",
                post.Id,
                [
                    new RecordFieldNode("Author", $"@{State.Handle}"),
                    new RecordFieldNode("Circle", post.Circle),
                    new RecordFieldNode("Content", post.Content),
                ],
                Subtitle: post.Content));
        }

        return new View("Post", new Column(items.ToArray()) { Gap = 12 });
    }

    private void Update(PostAppState next)
    {
        Set(next);
    }

    [DigitalBrain.Scenario("Publishing a post records it in ProfileNeuron with Circle visibility and renders feed")]
    public async Task VerifyPublishPostScenario()
    {
        await UpdateBio(new PostUpdateBio("vlad", "Building AI-native DigitalBrain apps", ["creator", "post"]));
        await Publish(new PostPublish("Hello DigitalBrain Circles!", "public", "post-1"));
        if (State.Handle != "vlad" || State.Posts.Length != 1 || State.Posts[0].Circle != "public")
        {
            throw new InvalidOperationException("Expected PostApp to persist bio and public post in ProfileNeuron.");
        }
    }
}
