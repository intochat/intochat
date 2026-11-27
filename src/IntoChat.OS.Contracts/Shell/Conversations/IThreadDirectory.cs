using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Flutter.Conversations;

// A friend's conversation this person sits in as a guest: where it is, what it is called and who invited them.
public sealed record ConversationInvite(BrainId Brain, ConversationId Conversation, string Title, AccountId By);

public sealed record ThreadEntry(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    bool Archived,
    string? ModelChoice = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NeuronId? Thread = null,
    ThreadTrustPosture TrustPosture = ThreadTrustPosture.OwnerTrusted,
    ThreadKind Kind = ThreadKind.DirectAssistant,
    long LastRevision = 0,
    long LastReadRevision = 0,
    int UnreadCount = 0,
    bool IsFavourite = false,
    bool IsPinned = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Folder = null,
    bool IsHost = true);

public sealed record ThreadPage(ThreadEntry[] Entries, int Total);

public sealed record ThreadShelf(
    ThreadEntry[] Favourites,
    ThreadEntry[] Pinned,
    string[] Folders,
    ThreadEntry[] Entries,
    int TotalUnread);

[Signal("ui.chat-index-changed")]
public sealed record ThreadDirectoryChanged(
    string Name,
    long Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NeuronId? Thread = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null) : Signal;

[Signal("social.rooms.room-changed")]
public sealed record ThreadRevisionChanged(NeuronId Thread, long Revision) : Signal;

// Unified directory of all threads a person owns or sits in (DirectAssistant, Squad, DirectPerson, SharedRoom),
// replacing SessionListNeuron + RoomsNeuron + InoConversationsNeuron.
[Shareable, NeuronAlias(Threads.Type), DefaultNeuronType(Threads.Type)]
public interface IThreadDirectory
{
    const string DefaultKey = "apps/ino/chats";

    [ReadOnlyCall]
    Task<ThreadPage> List(bool archived = false, int skip = 0, int take = Threads.MaxPage, ThreadKind? kind = null);

    [ReadOnlyCall]
    Task<ThreadShelf> Shelf();

    [ReadOnlyCall]
    Task<ThreadEntry?> Get(string id);

    Task<ThreadEntry> Create(
        string id,
        string title,
        ThreadTrustPosture posture = ThreadTrustPosture.OwnerTrusted,
        ThreadKind kind = ThreadKind.DirectAssistant);

    Task<NeuronId> CreateShared(string title, ThreadKind kind = ThreadKind.SharedRoom, string? threadKey = null);

    Task<ThreadEntry> Open(string id);

    [ReadOnlyCall]
    Task<string?> Current();

    Task Rename(string id, string title);

    Task Archive(string id);

    Task Unarchive(string id);

    Task Touch(string id, string? firstMessage);

    Task SetModel(string id, string? modelChoice);

    Task SetFavourite(string id, bool favourite);

    Task SetPinned(string id, bool pinned);

    Task SetFolder(string id, string? folder);

    Task MarkRead(string id, long revision);

    Task Leave(NeuronId thread);

    [Shared]
    Task Invited(NeuronId thread, string? title = null, ThreadKind? kind = null);

    [Shared]
    Task Changed(NeuronId thread, long revision);

    [ReadOnlyCall]
    Task<long> LastRevision(NeuronId thread);

    [Shared]
    Task InvitedToConversation(ConversationInvite invite);

    Task AdoptConversations(ConversationInvite[] invites);

    [ReadOnlyCall]
    Task<ConversationInvite[]> Conversations();
}

public static partial class Threads
{
    public const string Type = "chat-index";
    public const int MaxListed = 500;
    public const int MaxPage = 100;
    public const int TitleClip = 52;
    public const int TitleMax = 120;
    public const int FolderMax = 64;
    public const int ModelChoiceMax = 512;
    public const string NewChat = "New chat";
    public const string Untitled = "Untitled";

    [GeneratedRegex("^(main|c-[0-9a-f]{8}|c-[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalIdPattern();

    public static bool IsLocalId(string? id) => id is not null && LocalIdPattern().IsMatch(id);

    public static bool IsValidId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsControl);

    public static string EntryIdFor(BrainId ownerBrain, NeuronId thread)
    {
        if (thread.Brain == ownerBrain)
        {
            const string prefix = "conversations/";
            return thread.Key.StartsWith(prefix, StringComparison.Ordinal)
                ? thread.Key[prefix.Length..]
                : thread.Key;
        }
        return thread.FullKey();
    }

    public static string TitleFrom(string? message)
    {
        if (message is null) { return NewChat; }
        var text = new StringBuilder(Math.Min(message.Length, TitleClip * 4));
        var space = false;
        for (var at = 0; at < message.Length; at++)
        {
            var c = message[at];
            if (char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
            if (char.IsControl(c) || (Invisible(c) && !Joins(message, at))) { continue; }
            if (space) { text.Append(' '); space = false; }
            text.Append(c);
            if (text.Length > TitleClip * 4) { break; }
        }
        var title = Clip(text.ToString(), TitleClip).TrimEnd();
        return title.Length == 0 ? NewChat : title;
    }

    public static string ValidTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length is 0 or > TitleMax || trimmed.Any(char.IsControl)
            || Enumerable.Range(0, trimmed.Length).Any(at => Invisible(trimmed[at]) && !Joins(trimmed, at)))
        { throw new ArgumentException($"A thread title is one line of 1 to {TitleMax} visible characters.", nameof(title)); }
        return trimmed;
    }

    public static string? ValidFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) { return null; }
        var trimmed = folder.Trim();
        if (trimmed.Length > FolderMax || trimmed.Any(char.IsControl)
            || Enumerable.Range(0, trimmed.Length).Any(at => Invisible(trimmed[at]) && !Joins(trimmed, at)))
        { throw new ArgumentException($"A folder name is one line of 1 to {FolderMax} visible characters.", nameof(folder)); }
        return trimmed;
    }

    private static bool Invisible(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    private static bool Joins(string text, int at)
        => text[at] == '‍' && at > 0 && at < text.Length - 1 && Visible(text[at - 1]) && Visible(text[at + 1]);

    private static bool Visible(char c) => !char.IsWhiteSpace(c) && !char.IsControl(c) && !Invisible(c);

    private static string Clip(string text, int limit)
    {
        if (text.Length <= limit) { return text; }
        var length = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (length + element.Length > limit) { break; }
            length += element.Length;
        }
        return text[..length];
    }
}
