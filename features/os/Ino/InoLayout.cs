using System.Text;
using IntoChat.OS.Flutter.Chat;

// Pure sidebar rules: sizes, ordering, search and where the selection goes when a chat is archived.
public static class InoLayout
{
    public const double SidebarWidth = 260;
    public const double RailWidth = 56;
    public const double NarrowBreakpoint = 900;
    public const double HeaderHeight = 48;
    public const double ComposerHeight = 152;
    // The search field's slot: the kit field is 48 high inside a 4 px inset, so a 40 px slot overflowed.
    public const double SearchHeight = 56;

    // The first-run card's slot: the renderer has no auto-height slot, so it is sized for its tallest form.
    public const double FirstRunHeight = 340;

    // An overlay (modal, toast) draws above the surface; its layout slot is a hairline.
    public const double OverlayExtent = 1;

    // The client hides a toast after 4 s; the durable one is dropped then too, so a fresh mount never shows it stale.
    public static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(4);

    public const int QueryMax = 120;

    public const string ArchivedToast = "Chat archived.";
    public const string EmptyCopy = "Your chats will appear here.";
    public const string InvalidTitle = "Use a title of 1 to 120 characters.";

    public static string NoMatchCopy(string query) => "No chats match \"" + Literal(query) + "\".";

    public static IReadOnlyList<ChatIndexEntry> Newest(IEnumerable<ChatIndexEntry> chats)
        => [.. chats.Where(chat => !chat.Archived).OrderByDescending(chat => chat.UpdatedAt).ThenBy(chat => chat.Id, StringComparer.Ordinal)];

    // Literal, case-insensitive title match; an empty query lists everything.
    public static IReadOnlyList<ChatIndexEntry> Filter(IReadOnlyList<ChatIndexEntry> chats, string query)
    {
        var needle = query?.Trim() ?? "";
        return needle.Length == 0 ? chats : [.. chats.Where(chat => chat.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))];
    }

    // The newest other unarchived chat, or null when none remains (the caller starts a new one).
    public static string? NextSelection(IReadOnlyList<ChatIndexEntry> chats, string archivedId)
        => Newest(chats.Where(chat => chat.Id != archivedId)).FirstOrDefault()?.Id;

    // A search query as kept: no control or invisible format characters (bidi overrides, zero-width marks), at most QueryMax.
    public static string CleanQuery(string? query)
    {
        var kept = new string([.. (query ?? "").Where(c => !char.IsControl(c)
            && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)]);
        return kept.Length <= QueryMax ? kept : kept[..(char.IsHighSurrogate(kept[QueryMax - 1]) ? QueryMax - 1 : QueryMax)];
    }

    // Text nodes render markdown; a query is shown as typed, so its markdown characters are escaped.
    public static string Literal(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c)) { escaped.Append(' '); continue; }
            if ("\\`*_{}[]()<>#+-.!|~".Contains(c)) { escaped.Append('\\'); }
            escaped.Append(c);
        }
        return escaped.ToString();
    }
}
