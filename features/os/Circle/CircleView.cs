using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;

// Shared by every synapse of the circle app: the keys and the index surface, always rebuilt from the factory's list
// of this person's circle brains (BrainsOf on the trust side).
public static class CircleView
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static ICircleFactory Factory(DigitalBrainConnection brain) => brain.Get<ICircleFactory>(ICircleFactory.Key(Scope(brain)));

    public static Task Status(DigitalBrainConnection brain, string message) => brain.Get<IText>(Key(brain) + "/status").Set(message);

    // A refusal arrives as "The brain refused Contract.Method (422): reason"; the status line shows the reason's first line.
    public static Task Fail(DigitalBrainConnection brain, Exception error)
    {
        var message = error.Message;
        var reason = message.IndexOf("): ", StringComparison.Ordinal);
        if (message.StartsWith("The brain refused ", StringComparison.Ordinal) && reason > 0) { message = message[(reason + 3)..]; }
        var end = message.IndexOfAny(['\r', '\n']);
        return Status(brain, end < 0 ? message : message[..end]);
    }

    public static async Task Compose(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        var main = brain.Get<ILayout>(app + "/main");
        await main.Set(new("column", [new(UIVocabulary.TextType, app + "/status"), new("layout", app + "/circles")], Extents: [28, 0]), (await main.Read()).Revision);
        var surface = brain.Get<ISurface>(app + "/surface");
        await surface.Set(new("Circle", [new("layout", app + "/main")]), (await surface.Read()).Revision);
    }

    public static async Task Project(DigitalBrainConnection brain)
    {
        var cards = new List<UiChildRef>();
        foreach (var circle in await Factory(brain).Circles())
        {
            cards.Add(await Card(brain, "circle/" + circle.BrainId, circle.Name,
                "Circle brain " + circle.BrainId + ". Open it to talk, share an app, invite or accept."));
        }
        if (cards.Count == 0) { cards.Add(await Card(brain, "empty", "No circles yet", "Create one, or accept an invitation inside its circle brain.")); }
        var layout = brain.Get<ILayout>(Key(brain) + "/circles");
        await layout.Set(new("list", [.. cards]), (await layout.Read()).Revision);
    }

    static async Task<UiChildRef> Card(DigitalBrainConnection brain, string key, string title, string body)
    {
        var name = Key(brain) + "/cards/" + key;
        await brain.Get<ICard>(name).Set(title, body);
        return new(UIVocabulary.CardType, name);
    }
}
