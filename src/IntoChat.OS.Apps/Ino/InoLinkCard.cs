using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

// Input is app-owned and opaque: it is passed back to the app unchanged and never interpreted here.
internal sealed partial record InoLinkCard(
    string App,
    string Title,
    string Icon,
    bool Installed,
    string Input = "",
    string? Publisher = null,
    string? Release = null)
{
    public const int MaxInput = 2048;
    public const int MaxLinksKept = IInoRecords.MaxLinksKept;
    private const string LinkPrefix = "link-";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,79}\z", RegexOptions.ECMAScript)]
    private static partial Regex AppNameRegex();

    public static bool IsAppName(string? name) => name is not null && AppNameRegex().IsMatch(name);

    // A pasted link is posted (IConversation.Post) under a plain id; its app and opaque input are kept in Ino's own records
    // (IInoRecords), so neither enters the post id, the posted text or the transcript.
    public static string LinkPostId(Guid request) => LinkPrefix + request.ToString("N");

    public static bool TryReadLinkPost(string entryId, out Guid request)
    {
        request = Guid.Empty;
        const string posted = "post/" + LinkPrefix;
        return entryId.StartsWith(posted, StringComparison.Ordinal) && Guid.TryParseExact(entryId[posted.Length..], "N", out request);
    }

    private static List<(string Request, string App, string Input)> ParseLinks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return []; }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) { return []; }
            var links = new List<(string Request, string App, string Input)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { continue; }
                string? request = null, app = null, input = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) { continue; }
                    if (property.Name.Equals("request", StringComparison.OrdinalIgnoreCase)) { request = property.Value.GetString(); }
                    else if (property.Name.Equals("app", StringComparison.OrdinalIgnoreCase)) { app = property.Value.GetString(); }
                    else if (property.Name.Equals("input", StringComparison.OrdinalIgnoreCase)) { input = property.Value.GetString(); }
                }
                if (request is not null && app is not null)
                {
                    links.Add((request, app, input ?? ""));
                }
            }
            return links;
        }
        catch (JsonException) { return []; }
    }

    // Adds one link (a repeat of the same request keeps the first) and keeps the newest MaxLinksKept.
    public static string RememberLink(string? json, Guid request, string app, string input)
    {
        if (!IsAppName(app)) { throw new ArgumentException("Not an app name.", nameof(app)); }
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxInput) { throw new ArgumentException("The link input is too long.", nameof(input)); }
        var links = ParseLinks(json);
        var id = request.ToString("N");
        if (!links.Any(link => link.Request == id))
        {
            links.Add((id, app, input));
        }
        if (links.Count > MaxLinksKept) { links.RemoveRange(0, links.Count - MaxLinksKept); }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var link in links)
            {
                writer.WriteStartObject();
                writer.WriteString("request", link.Request);
                writer.WriteString("app", link.App);
                writer.WriteString("input", link.Input);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The card for a posted link, named by its app until the manifest names it; null when the store lost it.
    public static InoLinkCard? FindLink(string? json, Guid request)
    {
        var id = request.ToString("N");
        var links = ParseLinks(json);
        for (var i = links.Count - 1; i >= 0; i--)
        {
            var link = links[i];
            if (link.Request == id && IsAppName(link.App))
            {
                return new(link.App, link.App, "", true, link.Input);
            }
        }
        return null;
    }
}
