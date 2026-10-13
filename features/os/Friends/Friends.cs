using System.Globalization;
using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.Person;
using DigitalBrain.Flutter.Text;

public static class Friends
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public static async Task Present(DigitalBrainConnection brain)
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
        await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", ["unread"]), Side: new("collection", key + "/circles"), Main: new("text", key + "/about")));
    }

    public static async Task Post(DigitalBrainConnection brain, Guid requestId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var scope = Scope(brain);
        var key = scope + "/posts/" + requestId.ToString("N");
        var title = text[..Math.Min(60, text.Length)];
        var card = brain.Get<ICard>(key);
        if ((await card.Read()).Version == 0) { await card.Set(title, text); }
        var index = brain.Get<ICollectionView>(scope + "/posts");
        var previous = await index.Read();
        if (!previous.Definition.Items.Any(item => item.Id == key))
        {
            await index.Set(new([new(key, title, "post"), .. previous.Definition.Items],
                FileMode: false, SelectionEnabled: false, EmptyLabel: "No posts yet"), previous.Revision);
        }
        foreach (var circle in await brain.Get<ICircleFactory>(ICircleFactory.Key(scope)).Circles())
        { await brain.Get<ICircleChat>(ICircleChat.Key(circle.BrainId)).PublishPost(key, requestId.ToString()); }
    }
}
