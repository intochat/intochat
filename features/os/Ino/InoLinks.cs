using IntoChat.OS.AI.Agents;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;

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

    // How many times a concurrent writer of the links store is retried before the post fails.
    private const int StoreAttempts = 5;

    // A message that is a whole link becomes an app card and never a turn. After the conversation's active-run
    // check, the link's app and opaque input go to the links store and the card is posted under the plain id
    // link-<request>, with fixed copy as its text: the link reaches no model, post id, episode or status line.
    public static async Task<InoLinkOutcome> Handle(IAgent agent, IText links, IText status,
        Func<string, string, Task> advance, string message, Guid requestId)
    {
        if (!TryParse(message, out var app, out var input)) { return InoLinkOutcome.NotALink; }
        var id = ChatAppCard.LinkPostId(requestId);
        var state = await agent.Read();
        if (!state.Runs.Any(run => run.RunId == "post/" + id))
        {
            if (state.ActiveRun is not null)
            {
                await advance("attention", InoCopy.AnswerRunning);
                return InoLinkOutcome.Busy;
            }
            await Remember(links, requestId, app, input);
            await agent.Post(InoCopy.LinkPosted, id);
        }
        // The "Heard you" the request started with is settled: nothing else will answer it.
        await status.Set("");
        await advance("completed", InoCopy.LinkPosted);
        return InoLinkOutcome.Posted;
    }

    private static async Task Remember(IText links, Guid requestId, string app, string input)
    {
        for (var attempt = 0; attempt < StoreAttempts; attempt++)
        {
            var current = await links.Read();
            if (await links.TrySet(ChatAppCard.RememberLink(current.Markdown, requestId, app, input), current.Version)) { return; }
        }
        throw new InvalidOperationException("The links store kept changing.");
    }

    private static string? Decode(string component)
    {
        try { return Uri.UnescapeDataString(component.Replace('+', ' ')); }
        catch (UriFormatException) { return null; }
    }
}

public enum InoLinkOutcome { NotALink, Busy, Posted }
