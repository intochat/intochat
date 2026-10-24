namespace IntoChat.OS.Flutter.Chat;

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
    [property: Id(4)] string Input = "")
{
    public const int MaxInput = 2048;
    private const string LinkPrefix = "link-";
    private static readonly System.Text.RegularExpressions.Regex AppName = new("^[a-z0-9][a-z0-9-]{0,79}$");

    public static bool IsAppName(string? name) => name is not null && AppName.IsMatch(name);

    // A pasted link is posted (IAgent.Post) under an id that carries the app and its input, so the link
    // itself never enters the posted text the model reads: link-{request}.{app}.{base64url(input)}.
    public static string LinkPostId(Guid request, string app, string input)
    {
        if (!IsAppName(app)) { throw new ArgumentException("Not an app name.", nameof(app)); }
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxInput) { throw new ArgumentException("The link input is too long.", nameof(input)); }
        return LinkPrefix + request.ToString("N") + "." + app + "." + System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(input));
    }

    // Reads a posted run id ("post/" + LinkPostId) back; anything else is not a link post.
    public static bool TryReadLinkPost(string runId, out string app, out string input)
    {
        app = input = "";
        const string posted = "post/" + LinkPrefix;
        if (!runId.StartsWith(posted, StringComparison.Ordinal)) { return false; }
        var parts = runId[posted.Length..].Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) || !IsAppName(parts[1])) { return false; }
        try { input = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[2])); }
        catch (FormatException) { input = ""; return false; }
        app = parts[1];
        return true;
    }
}

[GenerateSerializer, Alias("ui.chat-activity")]
public sealed record ChatActivity(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Input,
    [property: Id(3)] string? Result = null,
    [property: Id(4)] string? Error = null);
