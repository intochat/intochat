using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.DevUI;

// DevUI's view of the caller's current Ino conversation: the same IConversation neuron Ino talks to, so a chat started here shows
// in Ino and the transcript lives in one place. Sends the newest user message via Say and polls Read(since) every 250 ms until
// the active run ends (S5 replaces polling with deltas).
internal sealed class NeuronAgentChatClient : IChatClient
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IServiceProvider _services;
    private readonly Func<NeuronId, ISessionList> _getSessions;
    private readonly Func<NeuronId, IConversation> _getConversation;
    private readonly TimeSpan _pollInterval;

    public NeuronAgentChatClient(IServiceProvider services)
        : this(
            services,
            id => SiloNeurons.Get<ISessionList>(services, id),
            id => SiloNeurons.Get<IConversation>(services, id),
            DefaultPollInterval)
    {
    }

    internal NeuronAgentChatClient(
        IServiceProvider services,
        Func<NeuronId, ISessionList> getSessions,
        Func<NeuronId, IConversation> getConversation,
        TimeSpan pollInterval)
    {
        _services = services;
        _getSessions = getSessions;
        _getConversation = getConversation;
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
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
        var before = await conversation.Read();
        var since = before.Revision;

        await conversation.Say(text);

        var emittedLength = 0;
        var seenToolStarts = new HashSet<string>(StringComparer.Ordinal);
        var seenToolFailures = new HashSet<string>(StringComparer.Ordinal);
        var seenEntries = new List<ConversationEntry>();
        string? targetRunId = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

                if (active.Reply is { Length: > 0 } partial && partial.Length > emittedLength)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, partial[emittedLength..]);
                    emittedLength = partial.Length;
                }

                await Task.Delay(_pollInterval, cancellationToken);
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

    // The newest conversation Ino uses for this brain; "main" when the session list has none yet.
    private async Task<NeuronId> ConversationKey()
    {
        var brain = ((_services.GetService(typeof(ICallerAmbient)) as ICallerAmbient)?.Current
            ?? throw new UnauthorizedAccessException("The request carries no caller.")).Brain.Value;
        var brainId = new BrainId(brain);
        var current = await _getSessions(new NeuronId(brainId, Sessions.Type, ISessionList.DefaultKey)).Current();
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
