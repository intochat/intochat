using System.Collections.Concurrent;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS;

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

    // A whole back-fill (every conversation of an upgraded brain) is abandoned after this long; agents already
    // indexed stay marked, so the next compose continues with the rest.
    public static readonly TimeSpan BackfillAllTimeout = TimeSpan.FromSeconds(20);

    // The conversation agents directly under {dataKey}/conversations/ with a chat id, newest first, at most MaxListed.
    public static IReadOnlyList<string> ConversationKeys(IEnumerable<DigitalBrain.BrainInstance> instances, string dataKey)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var prefix = dataKey + Conversations;
        return [.. instances
            .Where(instance => instance.Key.StartsWith(prefix, StringComparison.Ordinal) && ChatIndex.IsId(instance.Key[prefix.Length..]))
            .OrderByDescending(instance => instance.Created).Take(ChatIndex.MaxListed).Select(instance => instance.Key)];
    }

    // Upgrade back-fill: every conversation agent of this brain gets an entry, oldest first so the newest lists first.
    // Brains whose pass completed in this script activation, by Ino.DataKey.
    private static readonly ConcurrentDictionary<string, bool> AllIndexed = new(StringComparer.Ordinal);

    // One conversation's read is abandoned after this long; it counts as attempted, so the pass still completes.
    public static readonly TimeSpan AgentBackfillTimeout = TimeSpan.FromSeconds(2);

    // Once per brain per script activation once a pass has completed; a pass whose listing fails runs again on the next compose.
    public static Task<int> TryBackfillAll(IntoChatOSConnection brain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return TryBackfillAll(Ino.DataKey(brain), async token =>
        {
            var instances = new List<DigitalBrain.BrainInstance>();
            await foreach (var instance in brain.Kernel.Instances<IAgent>(token)) { instances.Add(instance); }
            return ConversationKeys(instances, Ino.DataKey(brain));
        }, Index(brain), agent => brain.Get<IAgent>(agent).Transcript(), cancellationToken);
    }

    public static async Task<int> TryBackfillAll(string dataKey, Func<CancellationToken, Task<IReadOnlyList<string>>> conversations, IChatIndex index,
        Func<string, Task<AiMessage[]>> transcript, CancellationToken cancellationToken, TimeSpan? perAgent = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        if (AllIndexed.ContainsKey(dataKey)) { return 0; }
        try
        {
            var keys = await conversations(cancellationToken);
            var done = await TryBackfillAll(index, keys, transcript, cancellationToken, perAgent);
            AllIndexed.TryAdd(dataKey, true);
            return done;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return 0; }
    }

    // Each conversation gets its own timeout; one that fails or hangs is skipped for this pass.
    public static async Task<int> TryBackfillAll(IChatIndex index, IReadOnlyList<string> newestFirst, Func<string, Task<AiMessage[]>> transcript,
        CancellationToken cancellationToken, TimeSpan? perAgent = null)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        var done = 0;
        for (var i = newestFirst.Count - 1; i >= 0; i--)
        {
            var agent = newestFirst[i];
            if (await Within(perAgent ?? AgentBackfillTimeout, token => TryBackfill(index, agent, transcript, token), cancellationToken)) { done++; }
        }
        return done;
    }

    // Runs work off the caller's path: the caller never waits, a failure is logged by type, and stopping cancels it.
    public static Task Background(string what, Func<CancellationToken, Task> work, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(async () =>
        {
            try { await work(stopping); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception error) { Console.Error.WriteLine("Ino: " + what + " failed (" + error.GetType().Name + ")"); }
        }, CancellationToken.None);
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
