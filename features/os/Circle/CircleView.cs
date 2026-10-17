using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Button;

public static class CircleView
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static ICircleFactory Factory(DigitalBrainConnection brain) => brain.Get<ICircleFactory>(ICircleFactory.Key(Scope(brain)));
    public static Task Status(DigitalBrainConnection brain, string message) => brain.Get<IText>(Key(brain) + "/status").Set(message);
    public static Task Fail(DigitalBrainConnection brain, Exception error) => Status(brain, error.Message.Split(['\r', '\n'])[0]);

    public static async Task Compose(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        await Factory(brain).BindPublicationButton();
        await PublicationUi.Layout(brain, "main", [new("layout", app + "/navigation-bar"), new("text", app + "/status"), new("layout", app + "/content"), new("layout", app + "/result")]);
        await brain.Get<IApp>(app).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", app + "/main"), Views: [new("full", new("layout", app + "/main"))]));
    }

    public static async Task Navigate(DigitalBrainConnection brain, PublicationUi.Navigation navigation)
    {
        if (navigation.Page is not ("feed" or "profile" or "circles" or "publication")) throw new ArgumentException("Unknown Circle view.");
        await Project(brain, navigation);
        await brain.Get<IText>(Key(brain) + "/navigation").Set(PublicationUi.Encode(navigation));
        await PublicationUi.Layout(brain, "result", []);
        await Status(brain, "");
    }

    public static async Task Restore(DigitalBrainConnection brain)
    {
        await Compose(brain);
        try { await Project(brain); }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await brain.Get<IText>(Key(brain) + "/navigation").Set("");
            await Project(brain);
            await Fail(brain, error);
        }
    }

    public static async Task Project(DigitalBrainConnection brain, PublicationUi.Navigation? target = null)
    {
        var app = Key(brain);
        var raw = (await brain.Get<IText>(app + "/navigation").Read()).Markdown;
        var nav = target ?? (string.IsNullOrEmpty(raw) ? new PublicationUi.Navigation() : PublicationUi.Decode<PublicationUi.Navigation>(raw));
        if (nav.Page is not ("feed" or "profile" or "circles" or "publication")) throw new ArgumentException("Unknown Circle view.");
        if (nav.Page == "profile" && string.IsNullOrEmpty(nav.Publisher))
        { nav = nav with { Publisher = (await Factory(brain).Publications(null)).PrincipalId }; }
        var feed = await Factory(brain).Publications(nav.Page == "profile" ? nav.Publisher : null);
        if (nav.Page == "publication" && (nav.Publication is null || !feed.Items.Any(item => item.Reference == nav.Publication)))
            throw new InvalidOperationException("This published release is unavailable. Your previous view and draft are saved.");
        async Task<UiChildRef> Link(string id, string label, PublicationUi.Navigation target) =>
            await PublicationUi.Button(brain, id, label, Scope(brain), "circle", "navigate", PublicationUi.Encode(target));
        await brain.Get<IButton>(app + "/account").SetActivation("Switch account", PublicationUi.Encode(new { kind = "account" }));
        await PublicationUi.Layout(brain, "navigation-bar", [
            await Link("feed", "Feed", new()),
            await Link("profile", "My profile", new("profile", feed.PrincipalId)),
            await Link("circles-link", "Circles", new("circles")),
            new("button", app + "/account")
        ], "row");
        var content = new List<UiChildRef>();
        if (nav.Page == "circles")
        {
            foreach (var circle in await Factory(brain).Circles())
                content.Add(await PublicationUi.Button(brain, "circle/" + circle.BrainId, circle.Name, circle.BrainId, "circleroom"));
            if (content.Count == 0) content.Add(await PublicationUi.Card(brain, "empty", "No circles yet", "Create a circle to build together."));
        }
        else if (nav.Page == "publication" && nav.Publication is { } reference)
        {
            var publication = feed.Items.SingleOrDefault(item => item.Reference == reference);
            if (publication is null) throw new InvalidOperationException("This published release is unavailable. Your selection and draft are saved.");
            content.Add(await Link("author", "@" + reference.Publisher, new("profile", reference.Publisher)));
            content.Add(await PublicationUi.ShareLink(brain, "Share this app", nav));
            content.AddRange(await PublicationUi.Preview(brain, publication, "fork-button", "Fork to my brain"));
            foreach (var circle in await Factory(brain).Circles())
                content.Add(await PublicationUi.Button(brain, "bring/" + circle.BrainId, "Bring into " + circle.Name, circle.BrainId, "circleroom", "publication", PublicationUi.Encode(reference)));
        }
        else
        {
            content.Add(await PublicationUi.Card(brain, "feed-heading", nav.Page == "profile" ? "@" + nav.Publisher : "Made to be shared",
                nav.Page == "profile" ? "Published apps. Explore an idea, then make it your own." : "Apps from the people building here. Preview one and fork your own copy."));
            if (nav.Page == "profile")
                content.Add(await PublicationUi.ShareLink(brain, "Share this profile", new("profile", nav.Publisher)));
            if (feed.UnavailablePublishers.Length > 0)
                content.Add(await PublicationUi.Card(brain, "unavailable", "Some apps are unavailable", "Refresh to try those publishers again."));
            const int pageSize = 24;
            var publications = feed.Items.OrderByDescending(item => item.PublishedAt)
                .ThenBy(item => item.Reference.Publisher, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.Name, StringComparer.Ordinal)
                .GroupBy(item => (item.Reference.Publisher, item.Reference.Source, item.Reference.Name))
                .Select(releases => releases.First()).ToArray();
            var offset = Math.Clamp(nav.Offset, 0, Math.Max(0, publications.Length - 1));
            foreach (var publication in publications.Skip(offset).Take(pageSize))
                content.Add(await PublicationUi.Post(brain, publication, Scope(brain)));
            if (offset > 0)
                content.Add(await Link("newer", "Newer apps", nav with { Offset = Math.Max(0, offset - pageSize) }));
            if (offset + pageSize < publications.Length)
                content.Add(await Link("older", "Older apps", nav with { Offset = offset + pageSize }));
            if (publications.Length == 0)
                content.Add(await PublicationUi.Card(brain, "empty", "Your first app starts here", "Publish an app from Build to share it on your profile."));
        }
        await PublicationUi.Layout(brain, "content", [.. content]);
    }
}
