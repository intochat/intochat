using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using DigitalBrain;

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

[App("circleroom")]
public sealed class CircleRoomApp : DigitalBrain.App
{
    [On("open")]
    public async Task Open()
    {
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        await Room.Compose(brain);
        await Room.Project(brain);
        var selected = (await brain.Get<IText>(Room.Key(brain) + "/navigation").Read()).Markdown;
        if (!string.IsNullOrEmpty(selected)) await Room.ShowPublication(brain, PublicationUi.Decode<AppPublicationRef>(selected));
    }

    [On("publication")]
    public async Task Publication(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        try { await Room.ShowPublication(brain, PublicationUi.Decode<AppPublicationRef>(input)); }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Room.Status(brain, error.Message); }
    }

    [On("say")]
    public async Task Say(string? input)
    {
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        try
        {
            var text = input?.Trim() ?? "";
            if (text.Length == 0) { throw new ArgumentException("There is nothing to say."); }
            await Room.Chat(brain).Say(text, Guid.NewGuid().ToString());
            await Room.Status(brain, "");
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        { await Room.Status(brain, error.Message); }
    }

    [On("share")]
    public async Task Share(string? input)
    {
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        try
        {
            var words = (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) { throw new ArgumentException("Name an app to share."); }
            await brain.Get<ICircle>(ICircle.Key(Room.Circle(brain))).Share(words[0], words[1..]);
            await Room.Status(brain, "Shared " + words[0] + ".");
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        { await Room.Status(brain, error.Message); }
    }
}
