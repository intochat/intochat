using System.Text;
using System.Text.RegularExpressions;
using Orleans.Concurrency;

namespace IntoChat.OS.Flutter.Chat;

// One entry per conversation of an assistant, at `{DataKey}/chats`. Each conversation stays an agent at
// `{DataKey}/conversations/{Id}`; the index only names, orders and archives them.
[Alias("chat-index"), Orleans.Metadata.DefaultGrainType(ChatIndex.GrainType)]
public interface IChatIndex : INeuron
{
    // Unarchived (archived: false) or archived (archived: true) entries, newest first; only the newest MaxListed are reachable.
    [ReadOnly] Task<ChatIndexPage> List(bool archived, int skip = 0, int take = ChatIndex.MaxPage);
    // Idempotent by id: an existing entry is returned unchanged.
    Task<ChatIndexEntry> Create(string id, string title);
    Task Rename(string id, string title);
    Task Archive(string id);
    Task Unarchive(string id);
    // Marks activity, creating the entry when missing; titles a still-default entry from the first message.
    Task Touch(string id, string? firstMessage);
    Task SetModel(string id, string? modelChoice);
    [ReadOnly] Task<ChatIndexEntry?> Get(string id);
}

[GenerateSerializer, Alias("ui.chat-index-entry")]
public sealed record ChatIndexEntry(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] DateTimeOffset UpdatedAt,
    [property: Id(3)] bool Archived,
    [property: Id(4)] string? ModelChoice = null);

[GenerateSerializer, Alias("ui.chat-index-page")]
public sealed record ChatIndexPage([property: Id(0)] ChatIndexEntry[] Entries, [property: Id(1)] int Total);

[GenerateSerializer, Alias("ui.chat-index-changed")]
public sealed record ChatIndexChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;

public static partial class ChatIndex
{
    public const string GrainType = "chat-index";
    public const int MaxListed = 500;
    public const int MaxPage = 100;
    public const int TitleClip = 52;
    public const int TitleMax = 120;
    public const int ModelChoiceMax = 512;
    public const string NewChat = "New chat";
    public const string Untitled = "Untitled";

    [GeneratedRegex("^(main|c-[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsId(string? id) => id is not null && IdPattern().IsMatch(id);

    // A title taken from a message: whitespace runs become one space, other control characters are dropped,
    // and the result is clipped to TitleClip without splitting a surrogate pair. Empty becomes "New chat".
    public static string TitleFrom(string? message)
    {
        if (message is null) { return NewChat; }
        var text = new StringBuilder(Math.Min(message.Length, TitleClip * 4));
        var space = false;
        foreach (var c in message)
        {
            if (char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
            if (char.IsControl(c)) { continue; }
            if (space) { text.Append(' '); space = false; }
            text.Append(c);
            if (text.Length > TitleClip) { break; }
        }
        if (text.Length > TitleClip)
        {
            var cut = char.IsHighSurrogate(text[TitleClip - 1]) ? TitleClip - 1 : TitleClip;
            text.Length = cut;
        }
        var title = text.ToString().TrimEnd();
        return title.Length == 0 ? NewChat : title;
    }

    // A title a person typed: trimmed, one line, no control characters, 1 to TitleMax characters.
    public static string ValidTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length is 0 or > TitleMax || trimmed.Any(char.IsControl))
        { throw new ArgumentException($"A chat title is one line of 1 to {TitleMax} characters.", nameof(title)); }
        return trimmed;
    }
}
