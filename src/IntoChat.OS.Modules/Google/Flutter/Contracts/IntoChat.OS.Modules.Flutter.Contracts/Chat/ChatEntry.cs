namespace IntoChat.OS.Modules.Flutter.Chat;

[GenerateSerializer, Alias("ui.chat-role")]
public enum ChatRole { User, Assistant }

[GenerateSerializer, Alias("ui.chat-entry")]
public sealed record ChatEntry([property: Id(0)] string Id, [property: Id(1)] ChatRole Role, [property: Id(2)] string Text)
{
    [Id(3)] public string Status { get; init; } = "completed";
    [Id(4)] public string? Error { get; init; }
    [Id(5)] public ChatActivity[] Activities { get; init; } = [];
    // The label of the model that answered, for the toolbar's "answered by" line.
    [Id(6)] public string? Model { get; init; }
    // An app this entry opened or a pasted workspace link names; the card's Open dispatches the workspace action.
    [Id(7)] public ChatAppCard? App { get; init; }
}

// Input is app-owned and opaque: it is passed back to the app unchanged and never interpreted here.
[GenerateSerializer, Alias("ui.chat-app-card")]
public sealed record ChatAppCard(
    [property: Id(0)] string App,
    [property: Id(1)] string Title,
    [property: Id(2)] string Icon,
    [property: Id(3)] bool Installed,
    [property: Id(4)] string Input = "",
    [property: Id(5)] string? Publisher = null,
    [property: Id(6)] string? Release = null)
{
    public const int MaxInput = 2048;
    private const string LinkPrefix = "link-";
    private static readonly System.Text.RegularExpressions.Regex AppName = new(@"^[a-z0-9][a-z0-9-]{0,79}\z", System.Text.RegularExpressions.RegexOptions.ECMAScript);

    public static bool IsAppName(string? name) => name is not null && AppName.IsMatch(name);

    public const int MaxLinksKept = 200;

    // A pasted link is posted (IAgent.Post) under a plain id; its app and opaque input are kept beside the
    // conversations, in the links store, so neither enters the post id, the posted text or the transcript.
    public static string LinkPostId(Guid request) => LinkPrefix + request.ToString("N");

    public static bool TryReadLinkPost(string runId, out Guid request)
    {
        request = Guid.Empty;
        const string posted = "post/" + LinkPrefix;
        return runId.StartsWith(posted, StringComparison.Ordinal) && Guid.TryParseExact(runId[posted.Length..], "N", out request);
    }

    // The store for a conversation agent at "{data}/conversations/{id}" is "{data}/links" (an IText of JSON).
    public static string? LinksKey(string conversationKey)
        => conversationKey.LastIndexOf("/conversations/", StringComparison.Ordinal) is var cut and > 0 ? conversationKey[..cut] + "/links" : null;

    private sealed record StoredLink(string Request, string App, string Input);

    private static List<StoredLink> Links(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return []; }
        try { return System.Text.Json.JsonSerializer.Deserialize<List<StoredLink>>(json, System.Text.Json.JsonSerializerOptions.Web) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    // Adds one link (a repeat of the same request keeps the first) and keeps the newest MaxLinksKept.
    public static string RememberLink(string? json, Guid request, string app, string input)
    {
        if (!IsAppName(app)) { throw new ArgumentException("Not an app name.", nameof(app)); }
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxInput) { throw new ArgumentException("The link input is too long.", nameof(input)); }
        var links = Links(json);
        var id = request.ToString("N");
        if (!links.Any(link => link.Request == id)) { links.Add(new(id, app, input)); }
        if (links.Count > MaxLinksKept) { links.RemoveRange(0, links.Count - MaxLinksKept); }
        return System.Text.Json.JsonSerializer.Serialize(links, System.Text.Json.JsonSerializerOptions.Web);
    }

    // The card for a posted link, named by its app until the manifest names it; null when the store lost it.
    public static ChatAppCard? FindLink(string? json, Guid request)
    {
        var id = request.ToString("N");
        return Links(json).LastOrDefault(link => link.Request == id) is { } found && IsAppName(found.App)
            ? new(found.App, found.App, "", true, found.Input) : null;
    }
}

[GenerateSerializer, Alias("ui.chat-activity")]
public sealed record ChatActivity(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Input,
    [property: Id(3)] string? Result = null,
    [property: Id(4)] string? Error = null);
