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

    public static bool TryIdOf(string? agentKey, out string id)
    {
        var at = agentKey?.LastIndexOf(Conversations, StringComparison.Ordinal) ?? -1;
        id = at < 0 ? "" : agentKey![(at + Conversations.Length)..];
        return ChatIndex.IsId(id);
    }

    // A hung index or transcript read is abandoned after this long; the next compose tries again.
    public static readonly TimeSpan BackfillTimeout = TimeSpan.FromSeconds(2);

    // Runs a back-fill for at most `timeout`; a timeout or failure is false, only the caller's cancellation throws.
    public static async Task<bool> Within(TimeSpan timeout, Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try { return await work(bounded.Token).WaitAsync(timeout, cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    // Conversations started before the index existed get an entry when Ino next composes. Never gates the
    // surface: any failure but cancellation is swallowed and the agent stays unmarked, so the next compose retries.
    public static async Task<bool> TryBackfill(IntoChatOSConnection brain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        try
        {
            if ((await brain.Get<IChat>(Ino.DataKey(brain) + "/transcript").Read()).Selection is not { } selection) { return false; }
            return await TryBackfill(Index(brain), selection.Agent, agent => brain.Get<IAgent>(agent).Transcript(), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public static async Task<bool> TryBackfill(IChatIndex index, string agentKey, Func<string, Task<AiMessage[]>> transcript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(transcript);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryIdOf(agentKey, out var id)) { return false; }
        if (Backfilled.ContainsKey(agentKey)) { return true; }
        try
        {
            if (await index.Get(id) is null)
            {
                var first = (await transcript(agentKey)).FirstOrDefault(message => message.Role == "user");
                var title = first is null ? ChatIndex.Untitled : Title(string.Concat(first.Content.OfType<AiText>().Select(part => part.Text)));
                await index.Create(id, title == ChatIndex.NewChat ? ChatIndex.Untitled : title);
            }
            Backfilled.TryAdd(agentKey, true);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}
