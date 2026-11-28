using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Voice;

// Cloud realtime function-call execution, per-session rate limiting, deduplication, and barge-in cancellation.
internal sealed class TalkToolDispatcher(
    Func<TalkEvent, CancellationToken, Task> sendEvent,
    IRealtimeClientSession session,
    Func<string, CancellationToken, Func<TalkTool, CancellationToken, Task>, Task<string>> executeBrainTurn,
    ILogger logger,
    int maxBrainTurnCalls = TalkToolDispatcher.MaxBrainTurnCallsPerSession)
{
    internal const string BrainTurnToolName = "run_brain_turn";
    internal const int MaxBrainTurnCallsPerSession = 24;
    private const int MaxHandledToolCallsPerSession = 64;

    private readonly ConcurrentDictionary<string, byte> _handledCallIds = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _brainTurnCount;
    private CancellationTokenSource? _currentCts;
    private Task? _currentTask;

    internal static TalkToolDispatcher ForSession(
        TalkWebSocketTransport transport,
        IRealtimeClientSession session,
        IConversation conversation,
        IIntoChatOSBrains brains,
        string brainId,
        ILogger logger)
        => new(
            transport.SendEventAsync,
            session,
            (request, ct, onTool) => ExecuteBrainTurnAsync(conversation, brains, brainId, request, ct, onTool),
            logger);

    internal static AITool CreateBrainTurnTool()
        => AIFunctionFactory.Create(
            (string request) => request,
            name: BrainTurnToolName,
            description: "Delegate a request to the OS brain (DeepSeek + OS tools, memory, web search, and apps). Use this whenever the user asks to run tools, inspect memory, search the web, or open/build apps.");

    internal static async Task<string> ExecuteBrainTurnAsync(
        IConversation conversation,
        IIntoChatOSBrains brains,
        string brainId,
        string request,
        CancellationToken ct,
        Func<TalkTool, CancellationToken, Task>? onToolEvent = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(brains);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);

        var watch = brains.Open(brainId);
        using var chat = new ConversationChatClient(conversation, watch, onToolEvent);
        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, request)], options: null, ct).ConfigureAwait(false);
        var text = response.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "Done." : text;
    }

    internal static string? ExtractToolRequest(FunctionCallContent call, string? fallbackHeard)
    {
        if (call.Arguments is { Count: > 0 } args)
        {
            if (args.TryGetValue("request", out var explicitArg) && TryReadNonEmpty(explicitArg, out var explicitText))
            {
                return explicitText;
            }

            foreach (var entry in args.Values)
            {
                if (TryReadNonEmpty(entry, out var text)) { return text; }
            }
        }

        return fallbackHeard;
    }

    public void DispatchInBackground(
        IReadOnlyList<FunctionCallContent> calls,
        Func<string?> peekHeard,
        Action markDelegated,
        CancellationToken sessionCt)
    {
        CancellationTokenSource? priorCts;
        var nextCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        lock (_gate)
        {
            priorCts = _currentCts;
            _currentCts = nextCts;
            _currentTask = Task.Run(async () =>
            {
                try
                {
                    if (await ExecuteCallsAsync(calls, peekHeard, markDelegated, nextCts.Token).ConfigureAwait(false)
                        && !nextCts.IsCancellationRequested)
                    {
                        await session.SendAsync(new CreateResponseRealtimeClientMessage(), nextCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    logger.LogSwallowed(error, "running background voice tool execution");
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_currentCts, nextCts)) { _currentCts = null; }
                    }

                    nextCts.Dispose();
                }
            }, CancellationToken.None);
        }

        CancelSafe(priorCts);
    }

    internal async Task<bool> ExecuteCallsAsync(
        IEnumerable<FunctionCallContent> calls,
        Func<string?> peekHeard,
        Action markDelegated,
        CancellationToken ct)
    {
        var executedAny = false;
        foreach (var call in calls)
        {
            ct.ThrowIfCancellationRequested();
            var callId = string.IsNullOrWhiteSpace(call.CallId) ? Guid.NewGuid().ToString("N") : call.CallId;
            if (_handledCallIds.ContainsKey(callId)
                || (_handledCallIds.Count < MaxHandledToolCallsPerSession && !_handledCallIds.TryAdd(callId, 0)))
            {
                continue;
            }

            executedAny = true;
            await sendEvent(new TalkState("thinking"), ct).ConfigureAwait(false);
            var resultText = await ExecuteSingleCallAsync(call, peekHeard, markDelegated, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            await session.SendAsync(
                new CreateConversationItemRealtimeClientMessage(
                    new RealtimeConversationItem([new FunctionResultContent(callId, resultText)])),
                ct).ConfigureAwait(false);
        }

        return executedAny;
    }

    public void CancelActive()
    {
        CancellationTokenSource? toCancel;
        lock (_gate) { toCancel = _currentCts; _currentCts = null; }
        CancelSafe(toCancel);
    }

    public async Task CancelAndDrainAsync()
    {
        CancellationTokenSource? toCancel;
        Task? toAwait;
        lock (_gate) { toCancel = _currentCts; _currentCts = null; toAwait = _currentTask; _currentTask = null; }

        CancelSafe(toCancel);
        if (toAwait is not null)
        {
            try { await toAwait.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<string> ExecuteSingleCallAsync(
        FunctionCallContent call,
        Func<string?> peekHeard,
        Action markDelegated,
        CancellationToken ct)
    {
        if (!string.Equals(call.Name, BrainTurnToolName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Tool '{call.Name}' is not available in voice mode. Use '{BrainTurnToolName}' to delegate OS actions.";
        }

        if (!TryReserveBrainTurn())
        {
            return "Voice session tool call limit reached. Ask the user to start a new voice session or continue in text chat.";
        }

        var request = ExtractToolRequest(call, peekHeard());
        if (string.IsNullOrWhiteSpace(request))
        {
            return "No request was provided for the brain turn.";
        }

        try
        {
            markDelegated();
            return await executeBrainTurn(request, ct, (tool, token) => sendEvent(tool, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            logger.LogSwallowed(error, "executing run_brain_turn from realtime voice session");
            return "I could not complete that action right now.";
        }
    }

    private bool TryReserveBrainTurn()
    {
        while (true)
        {
            var current = Volatile.Read(ref _brainTurnCount);
            if (current >= maxBrainTurnCalls) { return false; }
            if (Interlocked.CompareExchange(ref _brainTurnCount, current + 1, current) == current) { return true; }
        }
    }

    private static bool TryReadNonEmpty(object? value, out string text)
    {
        var raw = value is JsonElement json && json.ValueKind == JsonValueKind.String ? json.GetString() : value?.ToString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            text = raw.Trim();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static void CancelSafe(CancellationTokenSource? cts)
    {
        if (cts is null) { return; }
        try { cts.Cancel(); }
        catch (ObjectDisposedException error)
        {
            OsConsoleLog.For<TalkEvent>().LogSwallowed(error, "cancelling already-disposed voice tool token");
        }
    }
}
