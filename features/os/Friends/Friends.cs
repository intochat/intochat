using DigitalBrain;
using IntoChat.OS.Runtime;
using DigitalBrain.Spi;
using System.Globalization;
using IntoChat.OS;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Modules.Flutter.Layout;
using IntoChat.OS.Modules.Flutter.Person;
using IntoChat.OS.Modules.Flutter.Text;

public static partial class Friends
{
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Scope(IntoChatOSConnection brain) => brain.Setting("Brain")!;

    public static async Task Present(IntoChatOSConnection brain)
    {
        var scope = Scope(brain);
        var key = Key(brain);
        var profile = brain.Get<IPerson>(scope + "/profile");
        if (string.IsNullOrWhiteSpace((await profile.Read()).DisplayName)) { await profile.Set("Me", ""); }
        var circles = await brain.Get<ICircleFactory>(ICircleFactory.Key(scope)).Circles();
        var listing = brain.Get<ICollectionView>(key + "/circles");
        await listing.Set(new(circles.Select(circle => new CollectionItem(circle.BrainId, circle.Name, "circle", CanActivate: false)).ToArray(),
            FileMode: false, SelectionEnabled: false, EmptyLabel: "Join a circle to see friends and share posts."), (await listing.Read()).Revision);
        await brain.Get<IText>(key + "/about").Set("Friends are the people in your circles. Find their spheres at Home and open a friend to see their posts. Use Post to share with your circles.");
        var full = brain.Get<ILayout>(key + "/full");
        await full.Set(new("column", [new("text", key + "/about"), new("collection", key + "/circles")], Extents: [120, 0]), (await full.Read()).Revision);
        await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", ["unread"]), Side: new("collection", key + "/circles"), Main: new("text", key + "/about"), Views: [new("full", new("layout", key + "/full"))]));
    }

    public static Task Post(IntoChatOSConnection brain, Guid requestId, string text)
        => Post(brain, Scope(brain), requestId, text);

    public static Task Post(IntoChatOSConnection brain, string scope, Guid requestId, string text)
        => Post(scope, requestId, text, brain.Get<ICard>, brain.Get<ICollectionView>, brain.Get<ICircleFactory>, brain.Get<ICircleChat>);

    // The replay test holds an in-process brain, not a script connection. Both resolve neurons by os key.
    public static Task Post(INeuronProxies brain, string scope, Guid requestId, string text)
        => Post(scope, requestId, text, brain.Get<ICard>, brain.Get<ICollectionView>, brain.Get<ICircleFactory>, brain.Get<ICircleChat>);

    private static async Task Post(string scope, Guid requestId, string text, Func<string, ICard> cards, Func<string, ICollectionView> views, Func<string, ICircleFactory> circles, Func<string, ICircleChat> chats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var key = scope + "/posts/" + requestId.ToString("N");
        var title = text[..Math.Min(60, text.Length)];
        var card = cards(key);
        var existing = await card.Read();
        var index = views(scope + "/posts");
        var previous = await index.Read();
        var audience = views(IApp.Key(scope, "friends") + "/audiences/" + requestId.ToString("N"));
        var destinations = await audience.Read();
        if (destinations.Revision == 0)
        {
            // Freeze the audience before any post state or publication. Old posts without a snapshot
            // fail closed: today's circles cannot tell us whom the original author meant to address.
            var members = existing.Version != 0 || previous.Definition.Items.Any(item => item.Id == key)
                ? [] : await circles(ICircleFactory.Key(scope)).Circles();
            try
            {
                await audience.Set(new(members.Select(circle => new CollectionItem(circle.BrainId, circle.Name, "circle")).ToArray(),
                    FileMode: false, SelectionEnabled: false), 0);
            }
            catch (RevisionConflictException) { } // Another handling attempt already fixed this request's audience.
            destinations = await audience.Read();
        }
        if (existing.Version == 0) { await card.Set(title, text); }
        if (!previous.Definition.Items.Any(item => item.Id == key))
        {
            await index.Set(new([new(key, title, "post"), .. previous.Definition.Items],
                FileMode: false, SelectionEnabled: false, EmptyLabel: "No posts yet"), previous.Revision);
        }
        var factory = circles(ICircleFactory.Key(scope));
        var current = (await factory.Circles()).Select(circle => circle.BrainId).ToHashSet(StringComparer.Ordinal);
        foreach (var circle in destinations.Definition.Items.Where(circle => current.Contains(circle.Id)))
        {
            try { await chats(ICircleChat.Key(circle.Id)).PublishPost(key, requestId.ToString()); }
            catch (UnauthorizedAccessException)
            {
                // Removal can race the membership snapshot. Keep the publication guard authoritative;
                // only confirmed loss of this destination is skippable, not other authorization failures.
                current = (await factory.Circles()).Select(member => member.BrainId).ToHashSet(StringComparer.Ordinal);
                if (current.Contains(circle.Id)) { throw; }
            }
        }
    }
}
