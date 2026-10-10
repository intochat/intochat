using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;

// Shared by every synapse of the circle app: the keys, the selected circle and the surface, which is
// always rebuilt from the circle and chat neurons.
public static class CircleView
{
    public const int PlatformSharePercent = 25;

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static bool IsName(string value) => ICircle.IsValidName(value);

    // A brain's copy of a circle is "name" when it owns it and "name@owner" when it follows another brain's.
    public static string Plain(string reference) => reference.Contains('@') ? reference[..reference.IndexOf('@')] : reference;

    public static async Task<string?> Resolve(DigitalBrainConnection brain, string name)
    {
        var references = await Names(brain);
        return references.FirstOrDefault(item => item == name) ?? references.FirstOrDefault(item => item.StartsWith(name + "@", StringComparison.Ordinal));
    }

    public static async Task<string> Selected(DigitalBrainConnection brain) => (await brain.Get<IText>(Key(brain) + "/selected").Read()).Markdown;

    public static Task Select(DigitalBrainConnection brain, string name) => brain.Get<IText>(Key(brain) + "/selected").Set(name);

    public static Task Status(DigitalBrainConnection brain, string message) => brain.Get<IText>(Key(brain) + "/status").Set(message);

    // "name rest": the first word names a circle when it is one; otherwise the whole input goes to the selected circle.
    public static async Task<CircleInput> Split(DigitalBrainConnection brain, string input)
    {
        var trimmed = input.Trim();
        var space = trimmed.IndexOf(' ');
        if (space > 0 && await Resolve(brain, trimmed[..space]) is { } reference) { return new(reference, trimmed[(space + 1)..].Trim()); }
        return new(await Selected(brain), trimmed);
    }

    public static async Task<string[]> Names(DigitalBrainConnection brain)
    {
        var prefix = Scope(brain) + "/circles/";
        var names = new List<string>();
        await foreach (var instance in brain.Kernel.Instances<ICircle>())
        {
            if (instance.Key.StartsWith(prefix, StringComparison.Ordinal) && instance.Key.Length > prefix.Length && instance.Key.IndexOf('/', prefix.Length) < 0)
            { names.Add(instance.Key[prefix.Length..]); }
        }
        return [.. names.Order(StringComparer.Ordinal)];
    }

    public static ICircle Circle(DigitalBrainConnection brain, string reference) => brain.Get<ICircle>(Scope(brain) + "/circles/" + reference);
    public static ICircleChat Chat(DigitalBrainConnection brain, string reference) => brain.Get<ICircleChat>(ICircleChat.Key(Scope(brain) + "/circles/" + reference));

    public static async Task Compose(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        await brain.Get<IText>(app + "/platform").Set("Platform share: " + PlatformSharePercent + "%");
        var main = brain.Get<ILayout>(app + "/main");
        await main.Set(new("column", [new(UIVocabulary.TextType, app + "/platform"), new(UIVocabulary.TextType, app + "/status"),
            new("layout", app + "/circles")], Extents: [28, 28, 0]), (await main.Read()).Revision);
        var surface = brain.Get<ISurface>(app + "/surface");
        await surface.Set(new("Circle", [new("layout", app + "/main")]), (await surface.Read()).Revision);
    }

    public static async Task Project(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        var cards = new List<UiChildRef>();
        var selected = await Selected(brain);
        foreach (var name in await Names(brain))
        {
            CircleState state;
            try { state = await Circle(brain, name).State(); }
            catch (Exception) { continue; }
            if (state.Owner.Length == 0)
            {
                cards.Add(await Card(brain, "invitation/" + name, "Invitation: " + Plain(name),
                    "Invited by " + state.InvitedBy + ". Accept it with the accept operation."));
                continue;
            }
            if (!state.Members.Contains(Scope(brain))) { continue; }
            var lines = await Chat(brain, name).Read(0);
            var body = (name == selected ? "Selected\n" : "") +
                "Owner: " + state.Owner + "\n" +
                "Members: " + string.Join(", ", state.Members) + "\n" +
                (state.Invited.Length == 0 ? "" : "Invited: " + string.Join(", ", state.Invited) + "\n") +
                "Shared apps: " + (state.SharedApps.Length == 0 ? "none" : string.Join(", ", state.SharedApps)) + "\n" +
                "Compute share: " + state.ComputeSharePercent + "% of " + state.Owner + "'s budget";
            cards.Add(await Card(brain, "circle/" + name, Plain(name), body));
            cards.Add(await Card(brain, "chat/" + name, Plain(name) + " chat",
                lines.Length == 0 ? "No messages yet." : string.Join("\n", lines.OrderBy(line => line.Sequence).Select(line => line.Speaker + ": " + line.Text))));
        }
        if (cards.Count == 0) { cards.Add(await Card(brain, "empty", "No circles yet", "Create one, or wait for an invitation.")); }
        var layout = brain.Get<ILayout>(app + "/circles");
        await layout.Set(new("list", [.. cards]), (await layout.Read()).Revision);
    }

    static async Task<UiChildRef> Card(DigitalBrainConnection brain, string key, string title, string body)
    {
        var name = Key(brain) + "/cards/" + key;
        await brain.Get<ICard>(name).Set(title, body);
        return new(UIVocabulary.CardType, name);
    }
}

public sealed record CircleInput(string Name, string Rest);
