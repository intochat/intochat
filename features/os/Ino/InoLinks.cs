using IntoChat.OS.AI.Agents;
using IntoChat.OS.Flutter.Chat;

// A pasted workspace link, parsed with the shell's rules (app/shell/lib/workspace/workspace_link.dart):
// the route is /open, either as the fragment ("#/open?...", "https://host/#/open?...") or as the path, and
// application is an app name. Only a message that is the whole link counts; input stays opaque.
public static class InoLinks
{
    // A longer message is never treated as a link (its input could not be carried on the card).
    public const int MaxLength = 4096;

    public static string? Parse(string message) => TryParse(message, out var app, out _) ? app : null;

    public static bool TryParse(string? message, out string app, out string input)
    {
        app = input = "";
        var text = message?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxLength || text.Any(char.IsWhiteSpace)) { return false; }
        string? route;
        if (text.StartsWith('#')) { route = text[1..].StartsWith('/') ? text[1..] : null; }
        else if (text.StartsWith('/')) { route = text; }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var location) && location.Scheme is "http" or "https")
        {
            var fragment = location.Fragment.TrimStart('#');
            route = fragment.StartsWith('/') ? fragment : location.PathAndQuery;
        }
        else { return false; }
        if (route is null) { return false; }
        var cut = route.IndexOf('#');
        if (cut >= 0) { route = route[..cut]; }
        var question = route.IndexOf('?');
        if ((question < 0 ? route : route[..question]) != "/open" || question < 0) { return false; }
        string? name = null, value = null;
        foreach (var pair in route[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            var item = equals < 0 ? "" : Decode(pair[(equals + 1)..]);
            if (key is null || item is null) { return false; }
            // A repeated key is ambiguous, so it is not a link.
            if (key == "application") { if (name is not null) { return false; } name = item; }
            else if (key == "input") { if (value is not null) { return false; } value = item; }
        }
        if (!ChatAppCard.IsAppName(name) || (value?.Length ?? 0) > ChatAppCard.MaxInput) { return false; }
        app = name!;
        input = value ?? "";
        return true;
    }

    // Posts the card for a message that is a link and answers true: the caller starts no turn. The posted text
    // is fixed copy, so neither the link nor its input reaches the transcript the model reads.
    public static async Task<bool> PostCard(IAgent agent, string message, Guid requestId)
    {
        if (!TryParse(message, out var app, out var input)) { return false; }
        await agent.Post(InoCopy.LinkPosted, ChatAppCard.LinkPostId(requestId, app, input));
        return true;
    }

    private static string? Decode(string component)
    {
        try { return Uri.UnescapeDataString(component.Replace('+', ' ')); }
        catch (UriFormatException) { return null; }
    }
}
