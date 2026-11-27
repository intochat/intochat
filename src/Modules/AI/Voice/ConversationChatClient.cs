using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Signals.Watch;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

// Streams a spoken turn through IConversation.Say + S5's ephemeral TextDelta so voice turns run through the real
// conversation aggregate (tools, memory, masking) and cancelling the stream stops the active conversation run.
internal sealed class ConversationChatClient(
    IConversation conversation,
    ISignalWatch watch,
    Func<TalkTool, CancellationToken, Task>? onToolEvent = null) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var text = messages.LastOrDefault(static m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var deltaReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deltaPump = PumpAsync<TextDelta>(deltaReady, events.Writer, streamCts.Token);
        var changedPump = PumpAsync<ConversationChanged>(changedReady, events.Writer, streamCts.Token);

        var sayStarted = false;
        var completed = false;
        var stopRequested = 0;

        Task StopOnceAsync()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) != 0)
            {
                return Task.CompletedTask;
            }

            return StopConversationBestEffortAsync();
        }

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            if (Volatile.Read(ref sayStarted) && !Volatile.Read(ref completed))
            {
                _ = StopOnceAsync();
            }
        });

        try
        {
            await Task.WhenAll(deltaReady.Task, changedReady.Task).WaitAsync(cancellationToken).ConfigureAwait(false);

            var before = await conversation.Read().WaitAsync(cancellationToken).ConfigureAwait(false);
            var beforeRevision = before.Revision;
            var since = before.Revision;

            await conversation.Say(text).WaitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref sayStarted, true);
            if (cancellationToken.IsCancellationRequested)
            {
                await StopOnceAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            EntryId? targetEntry = null;
            string? targetRunId = null;
            var emittedLength = 0;
            var seenEntries = new List<ConversationEntry>();
            var emittedToolStarts = new HashSet<string>(StringComparer.Ordinal);
            var emittedToolFinishes = new HashSet<string>(StringComparer.Ordinal);

            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item is TextDelta delta)
                {
                    var deltaRun = EntryIds.RunOf(delta.Entry);
                    if (targetEntry is null)
                    {
                        if (targetRunId is not null
                            && !string.IsNullOrEmpty(deltaRun)
                            && !string.Equals(targetRunId, deltaRun, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        targetEntry = delta.Entry;
                        if (targetRunId is null && !string.IsNullOrEmpty(deltaRun))
                        {
                            targetRunId = deltaRun;
                        }
                    }
                    else if (delta.Entry != targetEntry.Value)
                    {
                        continue;
                    }

                    if (delta.At > emittedLength)
                    {
                        var heal = await conversation.Read(since).WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (heal.Revision > since) { since = heal.Revision; }
                        if (heal.Entries.Length > 0)
                        {
                            seenEntries.AddRange(heal.Entries);
                            await EmitToolEventsAsync(heal.Entries, emittedToolStarts, emittedToolFinishes, cancellationToken).ConfigureAwait(false);
                        }
                        if (heal.Active?.Reply is { Length: > 0 } stored && stored.Length > emittedLength)
                        {
                            yield return new ChatResponseUpdate(ChatRole.Assistant, stored[emittedLength..]);
                            emittedLength = stored.Length;
                        }
                    }

                    if (delta.At <= emittedLength && delta.At + delta.Delta.Length > emittedLength)
                    {
                        var slice = delta.At == emittedLength ? delta.Delta : delta.Delta[(emittedLength - delta.At)..];
                        if (slice.Length > 0)
                        {
                            yield return new ChatResponseUpdate(ChatRole.Assistant, slice);
                            emittedLength = delta.At + delta.Delta.Length;
                        }
                    }

                    continue;
                }

                if (item is not ConversationChanged changed)
                {
                    continue;
                }

                if (changed.Revision <= beforeRevision + 1)
                {
                    continue;
                }

                var view = await conversation.Read(since).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (view.Revision > since)
                {
                    since = view.Revision;
                }

                if (view.Entries.Length > 0)
                {
                    seenEntries.AddRange(view.Entries);
                    await EmitToolEventsAsync(view.Entries, emittedToolStarts, emittedToolFinishes, cancellationToken).ConfigureAwait(false);
                }

                targetRunId ??= view.Active?.RunId
                    ?? (seenEntries.OfType<Said>().LastOrDefault(static e => e.By is Participant.Human) is { } humanSaid
                        ? EntryIds.RunOf(humanSaid.Id)
                        : view.Runs.LastOrDefault(static r => !r.Posted)?.RunId);

                if (view.Active is not null || view.Revision <= beforeRevision)
                {
                    continue;
                }

                var finalReply = targetRunId is not null
                    ? seenEntries.OfType<Said>().LastOrDefault(e => e.By is Participant.Assistant && e.Id == EntryIds.Reply(targetRunId))
                    : seenEntries.OfType<Said>().LastOrDefault(static e => e.By is Participant.Assistant);

                if (finalReply is not null)
                {
                    if (finalReply.Text.Length > emittedLength)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, finalReply.Text[emittedLength..]);
                    }

                    Volatile.Write(ref completed, true);
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
                        Volatile.Write(ref completed, true);
                        throw new InvalidOperationException(closed.Error ?? "Conversation turn failed.");
                    }
                }

                Volatile.Write(ref completed, true);
                yield break;
            }
        }
        finally
        {
            if (Volatile.Read(ref sayStarted) && !Volatile.Read(ref completed))
            {
                await StopOnceAsync().ConfigureAwait(false);
            }

            await streamCts.CancelAsync().ConfigureAwait(false);
            await AwaitPumpAsync(deltaPump).ConfigureAwait(false);
            await AwaitPumpAsync(changedPump).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync<TSignal>(
        TaskCompletionSource ready,
        ChannelWriter<object> writer,
        CancellationToken ct) where TSignal : Signal
    {
        try
        {
            await foreach (var signal in watch.On<TSignal>(conversation, () =>
            {
                ready.TrySetResult();
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false))
            {
                writer.TryWrite(signal);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ready.TrySetCanceled(ct);
        }
        catch (Exception error)
        {
            OsConsoleLog.For<ConversationChatClient>().LogSwallowed(error, "watching conversation signals for voice turn");
            ready.TrySetException(error);
            writer.TryComplete(error);
        }
    }

    private async Task StopConversationBestEffortAsync()
    {
        try
        {
            await conversation.Stop().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<ConversationChatClient>().LogSwallowed(error, "stopping conversation run after talk cancellation");
        }
    }

    private static async Task AwaitPumpAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            OsConsoleLog.For<ConversationChatClient>().LogSwallowed(error, "shutting down conversation signal watch pump");
        }
    }

    private async Task EmitToolEventsAsync(
        ConversationEntry[] entries,
        HashSet<string> emittedStarts,
        HashSet<string> emittedFinishes,
        CancellationToken ct)
    {
        if (onToolEvent is null || entries.Length == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case Called called when emittedStarts.Add(called.Id.Value):
                    var startCallId = EntryIds.CallOf(called.Id) ?? called.Id.Value;
                    await onToolEvent(new TalkTool(startCallId, called.Tool, "running", FormatToolLabel(called.Tool)), ct).ConfigureAwait(false);
                    break;

                case Returned returned when emittedFinishes.Add(returned.Id.Value):
                    var endCallId = EntryIds.CallOf(returned.Id) ?? returned.Id.Value;
                    await onToolEvent(new TalkTool(endCallId, returned.Tool, returned.Ok ? "done" : "failed", FormatToolLabel(returned.Tool)), ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    internal static string FormatToolLabel(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "Tool";
        }

        var normalized = toolName.Trim().Replace('_', ' ').Replace('-', ' ');
        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}
