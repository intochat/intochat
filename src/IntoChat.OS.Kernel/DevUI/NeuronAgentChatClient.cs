using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Signalling;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.DevUI;

// DevUI's view of the caller's current Ino conversation: the same IConversation neuron Ino talks to, so a chat started here shows
// in Ino and the transcript lives in one place. Subscribes through SignalLog.Live for TextDelta and ConversationChanged, sends the
// newest user message via Say, yields each delta as it arrives, and ends when a ConversationChanged read shows Active == null.
internal sealed class NeuronAgentChatClient : IChatClient
{
    private const string TextDeltaAlias = "conversation.text-delta";
    private const string ChangedAlias = "conversation.changed";

    private readonly IServiceProvider _services;
    private readonly Func<NeuronId, IThreadDirectory> _getSessions;
    private readonly Func<NeuronId, IConversation> _getConversation;
    private readonly Func<NeuronId, string[], Func<Task>, CancellationToken, IAsyncEnumerable<SessionSignal>> _live;

    public NeuronAgentChatClient(IServiceProvider services)
        : this(
            services,
            id => SiloNeurons.Get<IThreadDirectory>(services, id),
            id => SiloNeurons.Get<IConversation>(services, id),
            (source, aliases, onSubscribed, ct) => SignalLog.Live(
                services,
                source,
                aliases,
                "devui/" + Guid.NewGuid().ToString("N"),
                onSubscribed,
                ct))
    {
    }

    internal NeuronAgentChatClient(
        IServiceProvider services,
        Func<NeuronId, IThreadDirectory> getSessions,
        Func<NeuronId, IConversation> getConversation,
        Func<NeuronId, string[], Func<Task>, CancellationToken, IAsyncEnumerable<SessionSignal>> live)
    {
        _services = services;
        _getSessions = getSessions;
        _getConversation = getConversation;
        _live = live;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) { updates.Add(update); }
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(text)) { yield break; }

        var key = await ConversationKey();
        (_services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?
            .CreateLogger<NeuronAgentChatClient>()
            .LogInformation("DevUI turn for conversation {Conversation} (caller brain {Brain}).", key, (_services.GetService(typeof(ICallerAmbient)) as ICallerAmbient)?.Current?.Brain.Value);

        var conversation = _getConversation(key);
        var beforeRevision = 0L;
        var since = 0L;
        var emittedLength = 0;
        var seenToolStarts = new HashSet<string>(StringComparer.Ordinal);
        var seenToolFailures = new HashSet<string>(StringComparer.Ordinal);
        var seenEntries = new List<ConversationEntry>();
        string? targetRunId = null;

        async Task OnSubscribed()
        {
            var before = await conversation.Read();
            beforeRevision = before.Revision;
            since = before.Revision;
            await conversation.Say(text);
        }

        await foreach (var entry in _live(key, [TextDeltaAlias, ChangedAlias], OnSubscribed, cancellationToken))
        {
            if (string.Equals(entry.Alias, TextDeltaAlias, StringComparison.Ordinal))
            {
                if (!TryReadDelta(entry.Payload, out var deltaEntry, out var deltaText, out var at))
                {
                    continue;
                }

                var deltaRun = EntryIds.RunOf(deltaEntry);
                if (targetRunId is null && !string.IsNullOrEmpty(deltaRun))
                {
                    targetRunId = deltaRun;
                }
                else if (targetRunId is not null && !string.IsNullOrEmpty(deltaRun)
                    && !string.Equals(targetRunId, deltaRun, StringComparison.Ordinal))
                {
                    continue;
                }

                if (at > emittedLength)
                {
                    var heal = await conversation.Read(since);
                    if (heal.Revision > since) { since = heal.Revision; }
                    if (heal.Entries.Length > 0) { seenEntries.AddRange(heal.Entries); }
                    if (heal.Active?.Reply is { Length: > 0 } stored && stored.Length > emittedLength)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, stored[emittedLength..]);
                        emittedLength = stored.Length;
                    }
                }

                if (at <= emittedLength && at + deltaText.Length > emittedLength)
                {
                    var slice = at == emittedLength ? deltaText : deltaText[(emittedLength - at)..];
                    if (slice.Length > 0)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, slice);
                        emittedLength = at + deltaText.Length;
                    }
                }
                continue;
            }

            if (!string.Equals(entry.Alias, ChangedAlias, StringComparison.Ordinal) && !SignalLog.IsGap(entry))
            {
                continue;
            }

            if (TryReadRevision(entry.Payload, out var changedRevision)
                && changedRevision <= beforeRevision + 1)
            {
                continue;
            }

            var view = await conversation.Read(since);
            if (view.Revision > since)
            {
                since = view.Revision;
            }
            if (view.Entries.Length > 0)
            {
                seenEntries.AddRange(view.Entries);
            }

            targetRunId ??= view.Active?.RunId
                ?? (seenEntries.OfType<Said>().LastOrDefault(static e => e.By is Participant.Human) is { } personSaid
                    ? EntryIds.RunOf(personSaid.Id)
                    : view.Runs.LastOrDefault(static r => !r.Posted)?.RunId);

            if (view.Active is { } active)
            {
                foreach (var call in active.ToolCalls)
                {
                    if (seenToolStarts.Add(call.CallId))
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"Using {call.Name} {call.Arguments}")]);
                    }
                    if (!string.IsNullOrWhiteSpace(call.Error) && seenToolFailures.Add(call.CallId))
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"{call.Name} failed: {call.Error}")]);
                    }
                }
                continue;
            }

            if (view.Revision <= beforeRevision)
            {
                continue;
            }

            // Active run has ended: yield any remaining text from the reply entry or closed run, or throw on failure.
            Said? finalReply = targetRunId is not null
                ? seenEntries.OfType<Said>().LastOrDefault(e => e.By is Participant.Assistant && e.Id == EntryIds.Reply(targetRunId))
                : seenEntries.OfType<Said>().LastOrDefault(static e => e.By is Participant.Assistant);

            if (finalReply is not null)
            {
                if (finalReply.Text.Length > emittedLength)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, finalReply.Text[emittedLength..]);
                }
                yield break;
            }

            var closed = targetRunId is not null
                ? view.Runs.LastOrDefault(r => string.Equals(r.RunId, targetRunId, StringComparison.Ordinal))
                : view.Runs.LastOrDefault(static r => !r.Posted);

            if (closed is not null)
            {
                if (closed.Reply is { Length: > 0 } partial && partial.Length > emittedLength)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, partial[emittedLength..]);
                }

                if (closed.Status is AgentRunStatus.Failed or AgentRunStatus.Interrupted)
                {
                    throw new InvalidOperationException(closed.Error ?? "Conversation turn failed.");
                }
            }

            yield break;
        }
    }

    private static bool TryReadDelta(JsonElement payload, out EntryId entry, out string delta, out int at)
    {
        entry = default;
        delta = string.Empty;
        at = 0;
        if (payload.ValueKind != JsonValueKind.Object) { return false; }

        if (payload.TryGetProperty("entry", out var entryEl))
        {
            if (entryEl.ValueKind == JsonValueKind.String && entryEl.GetString() is { Length: > 0 } rawEntry)
            {
                entry = new EntryId(rawEntry);
            }
            else if (entryEl.ValueKind == JsonValueKind.Object
                && entryEl.TryGetProperty("value", out var valEl)
                && valEl.ValueKind == JsonValueKind.String
                && valEl.GetString() is { Length: > 0 } nestedEntry)
            {
                entry = new EntryId(nestedEntry);
            }
        }

        if (payload.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String)
        {
            delta = deltaEl.GetString() ?? string.Empty;
        }

        if (payload.TryGetProperty("at", out var atEl) && atEl.TryGetInt32(out var offset))
        {
            at = offset;
        }

        return !string.IsNullOrEmpty(delta);
    }

    private static bool TryReadRevision(JsonElement payload, out long revision)
    {
        revision = 0;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("revision", out var revEl)
            && revEl.TryGetInt64(out revision);
    }

    // The newest conversation Ino uses for this brain; "main" when the session list has none yet.
    private async Task<NeuronId> ConversationKey()
    {
        var brain = ((_services.GetService(typeof(ICallerAmbient)) as ICallerAmbient)?.Current
            ?? throw new UnauthorizedAccessException("The request carries no caller.")).Brain.Value;
        var brainId = new BrainId(brain);
        var current = await _getSessions(new NeuronId(brainId, Threads.Type, IThreadDirectory.DefaultKey)).Current();
        var id = !string.IsNullOrWhiteSpace(current) && ConversationId.TryParse(current, out var parsed)
            ? parsed
            : ConversationId.Parse("main");
        return new NeuronId(brainId, IConversation.Type, IConversation.KeyOf(id));
    }

    internal static ChatResponseUpdate? Update(AgentTurnEvent turn) => turn switch
    {
        AgentTurnEvent.TextChunk chunk => new ChatResponseUpdate(ChatRole.Assistant, chunk.Content),
        AgentTurnEvent.ToolStarted started => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"Using {started.Name} {started.Arguments}")]),
        AgentTurnEvent.ToolFailed failed => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"{failed.Name} failed: {failed.Message}")]),
        AgentTurnEvent.TurnFailed failed => throw new InvalidOperationException(failed.Message),
        _ => null,
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
