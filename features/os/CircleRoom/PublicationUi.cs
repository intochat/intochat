using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Form;
using IntoChat.OS.Modules.Flutter.Layout;

public static class PublicationUi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Navigation(string Page = "feed", string? Publisher = null, AppPublicationRef? Publication = null);
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

    public static async Task<UiChildRef> Card(IntoChatOSConnection brain, string id, string title, string body)
    {
        var key = Key(brain) + "/" + id;
        await brain.Get<ICard>(key).Set(title, body);
        return new("card", key);
    }

    public static async Task Layout(IntoChatOSConnection brain, string id, UiChildRef[] children, string mode = "list")
    {
        var layout = brain.Get<ILayout>(Key(brain) + "/" + id);
        await layout.Set(new(mode, children), (await layout.Read()).Revision);
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
        return [
            await Card(brain, "publication", publication.Manifest.Title,
                "@" + reference.Publisher + " · " + reference.Name + " · " + reference.Version + "\nPublished " + publication.PublishedAt.ToString("u") + "\nRead-only preview · " + reference.Digest + "\n\n" + publication.Manifest.Description + "\nRequired connections: " +
                (publication.Manifest.Slots.Length == 0 ? "none" : string.Join(", ", publication.Manifest.Slots.Select(slot => slot.Name + " (" + slot.Connector + ")")))),
            await Card(brain, "preview", "Sample preview", publication.Preview ?? "No sample preview published."),
            new("form", FormKey(brain, reference)), new("button", Key(brain) + "/" + command)
        ];
    }

}
