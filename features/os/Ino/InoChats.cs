using System.Collections.Concurrent;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// The chat index's vocabulary for Ino: entry ids are the conversation agent's last key segment.
public static class InoChats
{
    private const string Conversations = "/conversations/";

    // Agents already checked by this script activation; a back-fill reads each conversation at most once.
    private static readonly ConcurrentDictionary<string, bool> Backfilled = new(StringComparer.Ordinal);

    public static string IndexKey(IntoChatOSConnection brain) => Ino.DataKey(brain) + "/chats";

    public static IChatIndex Index(IntoChatOSConnection brain) => brain.Get<IChatIndex>(IndexKey(brain));

    public static string Title(string? firstMessage) => ChatIndex.TitleFrom(firstMessage);

    public static string IdOf(string agentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        var at = agentKey.LastIndexOf(Conversations, StringComparison.Ordinal);
        return at < 0 ? throw new ArgumentException("Not a conversation agent key.", nameof(agentKey)) : agentKey[(at + Conversations.Length)..];
    }

    public static string AgentKey(string dataKey, string id) => dataKey + Conversations + id;

    // Conversations started before the index existed get an entry when Ino next composes.
    public static async Task Backfill(IntoChatOSConnection brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        if ((await brain.Get<IChat>(Ino.DataKey(brain) + "/transcript").Read()).Selection is not { } selection) { return; }
        await Backfill(Index(brain), selection.Agent, agent => brain.Get<IAgent>(agent).Transcript());
    }

    public static async Task Backfill(IChatIndex index, string agentKey, Func<string, Task<AiMessage[]>> transcript)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(transcript);
        var id = IdOf(agentKey);
        if (!ChatIndex.IsId(id) || Backfilled.ContainsKey(agentKey)) { return; }
        if (await index.Get(id) is not null) { Backfilled.TryAdd(agentKey, true); return; }
        var first = (await transcript(agentKey)).FirstOrDefault(message => message.Role == "user");
        var title = first is null ? ChatIndex.Untitled : Title(string.Concat(first.Content.OfType<AiText>().Select(part => part.Text)));
        await index.Create(id, title == ChatIndex.NewChat ? ChatIndex.Untitled : title);
        Backfilled.TryAdd(agentKey, true);
    }
}
