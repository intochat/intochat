using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Form;
using IntoChat.OS.Modules.Flutter.Layout;
using IntoChat.OS.Modules.Flutter.Text;

public static class PublicationUi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Navigation(string Page = "feed", string? Publisher = null, AppPublicationRef? Publication = null, int Offset = 0);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new ArgumentException("Missing selection.");
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string FormKey(IntoChatOSConnection brain, AppPublicationRef reference) => Key(brain) + "/forms/" + reference.Digest;

    public static async Task<UiChildRef> Button(IntoChatOSConnection brain, string id, string label, string brainId, string application, string? operation = null, string? input = null, string? view = null)
    {
        var key = Key(brain) + "/" + id;
        await brain.Get<IButton>(key).SetActivation(label, Encode(new { kind = "workspace", brainId, application, operation, input, view }));
        return new("button", key);
    }

    public static async Task<UiChildRef> ShareLink(IntoChatOSConnection brain, string label, Navigation navigation)
    {
        var key = Key(brain) + "/share-link";
        var url = "#/open?application=circle&input=" + Uri.EscapeDataString(Encode(navigation));
        await brain.Get<IText>(key).Set("[" + label + "](" + url + ") - open this link and copy its address to share.");
        return new("text", key);
    }

    public static async Task<UiChildRef> Card(IntoChatOSConnection brain, string id, string title, string body)
    {
        var key = Key(brain) + "/" + id;
        await brain.Get<ICard>(key).Set(title, body);
        return new("card", key);
    }

    public static async Task Layout(IntoChatOSConnection brain, string id, UiChildRef[] children, string mode = "list", string? surface = null, double padding = 0)
    {
        var layout = brain.Get<ILayout>(Key(brain) + "/" + id);
        await layout.Set(new(mode, children, Surface: surface, Padding: padding), (await layout.Read()).Revision);
    }

    public static async Task<UiChildRef[]> Preview(IntoChatOSConnection brain, AppPublication publication, string command, string label)
    {
        var reference = publication.Reference;
        var form = brain.Get<IForm>(FormKey(brain, reference));
        // Define resets values, so initialize only a previously unused form.
        if ((await form.Read()).Fields.Count == 0)
        {
            await form.Define(new("Your copy", [new("name", "App name", FormFieldKind.PlainText, Required: true)]));
            await form.SetDraft("name", reference.Name + "-copy");
        }
        await brain.Get<IButton>(Key(brain) + "/" + command).Set(label, Encode(reference));
        await brain.Get<IButton>(Key(brain) + "/" + command).SetVisual(new(Icon: "apps", Tone: "accent"));
        return [
            await Card(brain, "publication", publication.Manifest.Title, publication.Manifest.Description),
            await Card(brain, "preview", "About this app", publication.Preview ?? publication.Manifest.Description),
            await Card(brain, "connections", "Your own workspace",
                "Fork creates your own editable copy. Your connections and research stay in your brain." +
                (publication.Manifest.Slots.Length == 0 ? "" : "\nAvailable connections: " + string.Join(", ", publication.Manifest.Slots.Select(slot => slot.Name + " (" + slot.Connector + ")")))),
            new("form", FormKey(brain, reference)), new("button", Key(brain) + "/" + command)
        ];
    }

    public static async Task<UiChildRef> Post(IntoChatOSConnection brain, AppPublication publication, string scope)
    {
        var reference = publication.Reference;
        var id = "posts/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(reference)))).ToLowerInvariant();
        var navigation = Encode(new Navigation("publication", reference.Publisher, reference));
        var author = await Button(brain, id + "/author", "@" + reference.Publisher, scope, "circle", "navigate", Encode(new Navigation("profile", reference.Publisher)));
        var app = await Button(brain, id + "/app", publication.Manifest.Title, scope, "circle", "navigate", navigation);
        var bundle = await brain.Get<IApp>(IApp.Key(reference.Source.BrainId, reference.Name)).ExportRelease(reference.Version, reference.Digest);
        var artwork = publication.Manifest.Icon is { } icon ? bundle.Files.GetValueOrDefault(icon) : null;
        await brain.Get<IButton>(app.Name).SetVisual(new(Svg: artwork is { Length: <= 32000 } && artwork.TrimStart().StartsWith("<", StringComparison.Ordinal) ? artwork : null, Tone: "accent"));
        await brain.Get<IText>(Key(brain) + "/" + id + "/description").Set(publication.Manifest.Description);
        var description = new UiChildRef("text", Key(brain) + "/" + id + "/description");
        await brain.Get<IText>(Key(brain) + "/" + id + "/story").Set(publication.Preview ?? "Open the preview to explore this app and make it your own.");
        var story = new UiChildRef("text", Key(brain) + "/" + id + "/story");
        var preview = await Button(brain, id + "/preview", "Preview & fork", scope, "circle", "navigate", navigation);
        await brain.Get<IButton>(preview.Name).SetVisual(new(Icon: "apps", Tone: "accent"));
        await Layout(brain, id, [author, app, description, story, preview], "list", "panel", 24);
        return new("layout", Key(brain) + "/" + id);
    }
}
