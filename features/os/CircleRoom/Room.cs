using IntoChat.OS.Circles;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;

// Shared by every synapse of the circle room: the keys of the circle brain's own neurons and the transcript,
// always rebuilt from the chat.
public static class Room
{
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Circle(IntoChatOSConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static ICircleChat Chat(IntoChatOSConnection brain) => brain.Get<ICircleChat>(ICircleChat.Key(Circle(brain)));

    public static Task Status(IntoChatOSConnection brain, string message) => brain.Get<IText>(Key(brain) + "/status").Set(message);

    public static async Task Compose(IntoChatOSConnection brain)
    {
        var app = Key(brain);
        await brain.Get<ICircle>(ICircle.Key(Circle(brain))).BindPublicationButton();
        await PublicationUi.Layout(brain, "main", [new("text", app + "/status"), new("layout", app + "/publication-pane"), new("layout", app + "/result"), new("text", app + "/transcript")]);
        await brain.Get<IApp>(app).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", app + "/main"), Views: [new("full", new("layout", app + "/main"))]));
    }

    public static async Task ShowPublication(IntoChatOSConnection brain, AppPublicationRef reference)
    {
        var scope = reference.Source.BrainId;
        var releases = await brain.Get<IApp>(IApp.Key(scope, reference.Name)).PublishedReleases();
        var publication = releases.SingleOrDefault(item => item.Reference == reference)
            ?? throw new InvalidOperationException("This published release is unavailable.");
        await brain.Get<IText>(Key(brain) + "/navigation").Set(PublicationUi.Encode(reference));
        await PublicationUi.Layout(brain, "publication-pane", await PublicationUi.Preview(brain, publication, "bring-button", "Bring into this circle"));
    }

    public static async Task Project(IntoChatOSConnection brain)
    {
        var lines = await Chat(brain).Read(0);
        await brain.Get<IText>(Key(brain) + "/transcript").Set(lines.Length == 0 ? "No messages yet."
            : string.Join("\n", lines.OrderBy(line => line.Sequence).Select(line => line.Speaker + ": " + line.Text)));
    }
}
