using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.Text;

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
            new(new Sphere("icon.svg", []), Main: new("layout", app + "/main")));
    }

    public static async Task Navigate(DigitalBrainConnection brain, PublicationUi.Navigation navigation)
    {
        if (navigation.Page is not ("feed" or "profile" or "circles" or "publication")) throw new ArgumentException("Unknown Circle view.");
        await brain.Get<IText>(Key(brain) + "/navigation").Set(PublicationUi.Encode(navigation));
        await Project(brain);
    }

    public static async Task Project(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        var raw = (await brain.Get<IText>(app + "/navigation").Read()).Markdown;
        var nav = string.IsNullOrEmpty(raw) ? new PublicationUi.Navigation() : PublicationUi.Decode<PublicationUi.Navigation>(raw);
        var feed = await Factory(brain).Publications(nav.Page == "profile" ? nav.Publisher : null);
        async Task<UiChildRef> Link(string id, string label, PublicationUi.Navigation target) =>
            await PublicationUi.Button(brain, id, label, Scope(brain), "circle", "navigate", PublicationUi.Encode(target));
        await PublicationUi.Layout(brain, "navigation-bar", [
            await Link("feed", "Feed", new()),
            await Link("profile", "My profile", new("profile", feed.PrincipalId)),
            await Link("circles-link", "Circles", new("circles"))
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
            content.AddRange(await PublicationUi.Preview(brain, publication, "fork-button", "Fork to my brain"));
            foreach (var circle in await Factory(brain).Circles())
                content.Add(await PublicationUi.Button(brain, "bring/" + circle.BrainId, "Bring into " + circle.Name, circle.BrainId, "circleroom", "publication", PublicationUi.Encode(reference)));
        }
        else
        {
            var collection = brain.Get<ICollectionView>(app + "/feed-items");
            var title = nav.Page == "profile" ? "@" + nav.Publisher : "Feed";
            await collection.Set(new(feed.Items.Select(item => new CollectionItem(
                PublicationUi.Encode(new PublicationUi.Navigation("publication", item.Reference.Publisher, item.Reference)),
                item.Manifest.Title, "app", "@" + item.Reference.Publisher + " · " + item.Reference.Version + " · " + item.Manifest.Description,
                ModifiedAt: item.PublishedAt, TrailingText: "Preview")).ToArray(),
                FileMode: false, EmptyLabel: "No published apps yet. Publish an app from Build to share it here.",
                Breadcrumbs: [new(PublicationUi.Encode(new PublicationUi.Navigation()), "Circle"), new(PublicationUi.Encode(nav), title)],
                Error: feed.UnavailablePublishers.Length == 0 ? null : "Some publishers are unavailable. Refresh to retry."), (await collection.Read()).Revision);
            content.Add(new("collection", app + "/feed-items"));
        }
        await PublicationUi.Layout(brain, "content", [.. content]);
    }
}
