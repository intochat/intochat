using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Kernel;
using DigitalBrain.Signals;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// The sphere's fold. It holds brain-wide synapses on conversation changes, session changes, legacy agent turn signals,
// context changes, the confirmation inbox and the flows index, and derives one Presence with a fixed precedence:
// a trusted interrupt pauses; an approval Ino waits on; a tool in flight (searching for search/read tools); a turn
// under 2 s old with no tool listens; a failed Ino flow under 10 s old needs attention; else idle.
[DigitalBrain.Neurons.Neuron("ino.presence")]
internal sealed class PresenceNeuron(
    ISignalHistory history,
    ILogger<PresenceNeuron>? logger = null,
    ISecretRedactor? redactor = null,
    IServiceProvider? services = null) : DigitalBrain.Neurons.Neuron<Presence>, IPresence
{
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(2), AttentionWindow = TimeSpan.FromSeconds(10);

    // The app id FlowsNeuron records a flow an agent starts under; such a flow is Ino's.
    private const string AssistantApp = IntoChat.OS.Modules.Identity.Contracts.Grants.CallerApps.Assistant;

    private const string WindowTimer = "presence.window";

    private sealed record Run(string RunId, DateTimeOffset Started, string? TraceId, string? Tool, string? Arguments);

    private sealed record Wait(string? FlowId, string? Step, string? FlowType, string Reason);

    private sealed record Failure(string FlowId, string FlowType, string? Detail, DateTimeOffset At);

    private sealed record PendingReceipt(string ConversationKey, string ReceiptId, string Text);

    private readonly Dictionary<(string Context, string Name), string> _interrupts = [];
    private readonly List<PendingReceipt> _pendingReceipts = [];
    private string? _conversationId;
    private Run? _run;
    private string? _lastTrace;
    private DateTimeOffset _turnStarted = DateTimeOffset.MinValue;
    private bool _blocked;
    private ContextEntry? _focus;
    private (string Context, string Name)? _focusAt;
    private Wait? _wait;
    private bool _inboxOpen;
    private Failure? _failure;

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Wires.EnsureBrainWide([
            .. AgentSignals.All,
            typeof(ConversationChanged),
            typeof(SessionsChanged),
            typeof(ContextUpdated),
            typeof(ContextRemoved),
            typeof(ConfirmationRequested),
            typeof(ConfirmationResolved),
            typeof(FlowsChanged),
            typeof(FlowWaiting),
            typeof(FlowDone),
        ]);
        await Quietly(async () => _conversationId = await Get<ISessionList>(new NeuronId(Id.Brain, Sessions.Type, ISessionList.DefaultKey)).Current(), "sessions");
        await Rebuild();
    }

    protected override Task OnReminder(string name, CancellationToken cancellationToken) => name == WindowTimer ? Recompute() : Task.CompletedTask;

    // Re-reads what the fold keeps in memory: the current conversation's active run, blocked flag, contexts and approvals; then recomputes.
    private async Task Rebuild()
    {
        _run = null;
        _lastTrace = null;
        _blocked = false;
        _interrupts.Clear();
        _focus = null;
        _focusAt = null;
        string[] contexts = [IFocus.ContextKey];
        if (!string.IsNullOrWhiteSpace(_conversationId) && ConversationId.TryParse(_conversationId, out var convId))
        {
            await Quietly(async () =>
            {
                var view = await Get<IConversation>(new NeuronId(Id.Brain, IConversation.Type, IConversation.KeyOf(convId))).Read();
                ApplyConversationView(view);
                contexts = [.. contexts, .. view.Contexts];
            }, "conversation");
        }
        var prefix = Id.Brain.Value + "/";
        foreach (var key in contexts.Select(key => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key).Distinct(StringComparer.Ordinal))
        {
            await Quietly(() => ReadContextEntries(prefix + key), "context");
        }
        await Quietly(ReadApprovals, "approvals");
        await Recompute();
    }

    private void ApplyConversationView(ConversationView view)
    {
        _blocked = view.Blocked;
        if (view.Active is { } active)
        {
            var tool = active.ToolCalls.LastOrDefault(call => call is { Result: null, Code: null, Error: null });
            _run = new Run(active.RunId, active.StartedAt, active.TraceId, tool?.Name, tool?.Arguments);
        }
        else
        {
            _run = null;
        }
        _lastTrace = view.Active?.TraceId ?? view.Runs.LastOrDefault(run => run.TraceId is not null)?.TraceId ?? _lastTrace;
        var started = view.Active?.StartedAt ?? view.Runs.LastOrDefault()?.StartedAt;
        if (started is { } at && at > _turnStarted) { _turnStarted = at; }
    }

    public Task<Presence> Read(CancellationToken ct = default) => Task.FromResult(State);

    [On]
    public async Task OnSessionsChanged(SessionsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        string? next = _conversationId;
        await Quietly(async () => next = await Get<ISessionList>(new NeuronId(Id.Brain, Sessions.Type, ISessionList.DefaultKey)).Current(), "sessions");
        if (string.Equals(next, _conversationId, StringComparison.Ordinal)) { return; }
        _conversationId = next;
        await Rebuild();
    }

    [On]
    public async Task OnConversationChanged(ConversationChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        await FlushPendingReceiptsAsync();
        if (string.IsNullOrWhiteSpace(_conversationId) || !string.Equals(changed.Id.Value, _conversationId, StringComparison.Ordinal))
        {
            return;
        }

        await Quietly(async () =>
        {
            var view = await Get<IConversation>(new NeuronId(Id.Brain, IConversation.Type, IConversation.KeyOf(changed.Id))).Read();
            ApplyConversationView(view);
        }, "conversation");
        await Recompute();
    }

    [On]
    public Task OnTurnStarted(TurnStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        if (!IsCurrent(started.AgentId)) { return Task.CompletedTask; }
        var now = Context.Time.GetUtcNow();
        _run = new Run(started.RunId, now, started.TraceId ?? DigitalBrain.Capabilities.Compute.TraceContext.Current?.TraceId, null, null);
        _lastTrace = _run.TraceId ?? _lastTrace;
        _turnStarted = now;
        return Recompute();
    }

    [On]
    public Task OnTurnProgressed(TurnProgressed progressed)
    {
        ArgumentNullException.ThrowIfNull(progressed);
        if (!IsCurrent(progressed.AgentId) || _run is not { } run || run.RunId != progressed.RunId) { return Task.CompletedTask; }
        _run = progressed.Kind switch
        {
            "tool-call" => run with { Tool = progressed.Tool ?? "", Arguments = progressed.Payload },
            "tool-result" => run with { Tool = null, Arguments = null },
            _ => run,
        };
        return Recompute();
    }

    [On]
    public Task OnTurnCompleted(TurnCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return Ended(completed.AgentId, completed.RunId);
    }

    [On]
    public Task OnTurnFailed(TurnFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        return Ended(failed.AgentId, failed.RunId);
    }

    [On]
    public Task OnAgentBlocked(AgentBlocked blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (!IsCurrent(blocked.Agent)) { return Task.CompletedTask; }
        _blocked = blocked.Blocked;
        return Recompute();
    }

    // Part of the agent's signal set; nothing the sphere shows.
    [On]
    public Task OnAgentConfigured(AgentConfigured configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return Task.CompletedTask;
    }

    [On]
    public Task OnTranscriptCleared(TranscriptCleared cleared)
    {
        ArgumentNullException.ThrowIfNull(cleared);
        return Task.CompletedTask;
    }

    [On]
    public async Task OnContextUpdated(ContextUpdated updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        await Quietly(() => ReadContext(updated.ContextId, updated.Name), "context");
        await Recompute();
    }

    [On]
    public Task OnContextRemoved(ContextRemoved removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var at = (removed.ContextId, removed.Name);
        var changed = _interrupts.Remove(at);
        if (_focusAt == at) { _focus = null; _focusAt = null; changed = true; }
        return changed ? Recompute() : Task.CompletedTask;
    }

    [On]
    public async Task OnConfirmationRequested(ConfirmationRequested requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        await Quietly(ReadApprovals, "approvals");
        await Recompute();
    }

    [On]
    public async Task OnConfirmationResolved(ConfirmationResolved resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        await Quietly(ReadApprovals, "approvals");
        await Recompute();
    }

    // A flow's wait reaches the index after its plan reaches the inbox, so the approvals are re-read when the index changes while the inbox holds any plan.
    [On]
    public async Task OnFlowsChanged(FlowsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (!_inboxOpen) { return; }
        await Quietly(ReadApprovals, "approvals");
        await Recompute();
    }

    [On]
    public async Task OnFlowWaiting(FlowWaiting waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (waiting.Reason != WaitReason.Approval) { return; }
        FlowView? flow = null;
        await Quietly(async () => flow = await Get<IFlows>(IFlows.Key(Id.Brain.Value)).Read(waiting.FlowId), "flow");
        if (flow is null || !IsInoOwned(flow)) { return; }
        var receiptText = await BuildRedactedReceiptAsync(
            flow,
            outcomeLabel: "Needs your decision",
            rawDetail: $"Waiting on step '{waiting.Step}' for your answer.");
        await PostFlowReceiptAsync(flow, $"flow-wait/{waiting.FlowId}/{waiting.Step}", receiptText);
    }

    [On]
    public async Task OnFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        FlowView? flow = null;
        await Quietly(async () => flow = await Get<IFlows>(IFlows.Key(Id.Brain.Value)).Read(done.FlowId), "flow");
        if (flow is null || !IsInoOwned(flow)) { return; }

        var rawDetail = done.Detail ?? flow.Detail;
        if (done.Outcome == FlowOutcome.Failed)
        {
            _failure = new Failure(done.FlowId, flow.Type, rawDetail, Context.Time.GetUtcNow());
            await Recompute();
        }

        var outcomeLabel = done.Outcome switch
        {
            FlowOutcome.Completed => "Completed",
            FlowOutcome.Failed => "Failed",
            _ => "Cancelled",
        };
        var receiptText = await BuildRedactedReceiptAsync(flow, outcomeLabel, rawDetail);
        await PostFlowReceiptAsync(flow, $"flow-done/{done.FlowId}", receiptText);
    }

    private ISecretRedactor? ResolveRedactor()
        => redactor ?? services?.GetService(typeof(ISecretRedactor)) as ISecretRedactor;

    // Fail-closed redaction: if ISecretRedactor is unavailable or throws, never leak raw task output/detail.
    private async Task<string> BuildRedactedReceiptAsync(FlowView flow, string outcomeLabel, string? rawDetail)
    {
        var activeRedactor = ResolveRedactor();
        var title = string.IsNullOrWhiteSpace(flow.Title) ? flow.Type : flow.Title;
        var baseText = string.IsNullOrWhiteSpace(rawDetail)
            ? $"Background task \"{title}\" ({outcomeLabel})."
            : $"Background task \"{title}\" ({outcomeLabel}): {rawDetail.Trim()}";

        if (activeRedactor is null)
        {
            return $"Background task \"{title}\" ({outcomeLabel}).";
        }

        try
        {
            var redacted = await activeRedactor.Redact(baseText, CancellationToken.None).ConfigureAwait(true);
            return string.IsNullOrWhiteSpace(redacted)
                ? $"Background task \"{title}\" ({outcomeLabel})."
                : redacted;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            PresenceLog.Unread(Logger(), error, "secret-redactor");
            return $"Background task \"{title}\" ({outcomeLabel}). [details redacted]";
        }
    }

    private async Task PostFlowReceiptAsync(FlowView flow, string receiptId, string text)
    {
        if (string.IsNullOrWhiteSpace(flow.Conversation))
        {
            return;
        }

        var conversationKey = InoFileWriteReports.ConversationKey(Id.Brain.Value, flow.Conversation);
        try
        {
            await Get<IConversation>(new NeuronId(Id.Brain, IConversation.Type, conversationKey))
                .Post(text, receiptId, Visibility.Owner);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            PresenceLog.Unread(Logger(), error, "flow-receipt");
            if (_pendingReceipts.All(r => !string.Equals(r.ReceiptId, receiptId, StringComparison.Ordinal)))
            {
                _pendingReceipts.Add(new PendingReceipt(conversationKey, receiptId, text));
            }
        }
    }

    private async Task FlushPendingReceiptsAsync()
    {
        if (_pendingReceipts.Count == 0) { return; }
        foreach (var receipt in _pendingReceipts.ToArray())
        {
            try
            {
                await Get<IConversation>(new NeuronId(Id.Brain, IConversation.Type, receipt.ConversationKey))
                    .Post(receipt.Text, receipt.ReceiptId, Visibility.Owner);
                _pendingReceipts.Remove(receipt);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                PresenceLog.Unread(Logger(), error, "flow-receipt");
            }
        }
    }

    private Task Ended(string agentId, string runId)
    {
        if (!IsCurrent(agentId) || _run is not { } run || run.RunId != runId) { return Task.CompletedTask; }
        _lastTrace = run.TraceId ?? _lastTrace;
        _run = null;
        return Recompute();
    }

    private bool IsCurrent(string agentOrConversationKey)
    {
        if (string.IsNullOrWhiteSpace(_conversationId)) { return false; }
        return string.Equals(agentOrConversationKey, _conversationId, StringComparison.Ordinal)
            || string.Equals(agentOrConversationKey, "ino/conversations/" + _conversationId, StringComparison.Ordinal)
            || string.Equals(agentOrConversationKey, "conversations/" + _conversationId, StringComparison.Ordinal);
    }

    private static bool IsInoOwned(FlowView flow) => flow.App == AssistantApp;

    private bool IsCurrentConversation(FlowView flow)
    {
        if (string.IsNullOrWhiteSpace(_conversationId) || string.IsNullOrWhiteSpace(flow.Conversation)) { return false; }
        var convKey = "conversations/" + _conversationId;
        var legacyKey = "ino/conversations/" + _conversationId;
        return flow.Conversation == convKey
            || flow.Conversation == legacyKey
            || flow.Conversation == new NeuronId(Id.Brain, IConversation.Type, convKey).FullKey()
            || flow.Conversation == new NeuronId(Id.Brain, "agent", legacyKey).FullKey();
    }

    // Reads every entry of a context of this brain (keys "{brain}/{key}").
    private async Task ReadContextEntries(string contextId)
    {
        var prefix = Id.Brain.Value + "/";
        if (!contextId.StartsWith(prefix, StringComparison.Ordinal)) { return; }
        var snapshot = await Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", contextId[prefix.Length..])).Read();
        foreach (var entry in snapshot.Entries) { Keep(contextId, entry.Name, entry); }
    }

    // Reads one entry of a context of this brain and keeps it when it is a trusted interrupt or the ui.focus.
    private async Task ReadContext(string contextId, string name)
    {
        var prefix = Id.Brain.Value + "/";
        if (!contextId.StartsWith(prefix, StringComparison.Ordinal)) { return; }
        var snapshot = await Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", contextId[prefix.Length..])).Read();
        Keep(contextId, name, snapshot.Entries.FirstOrDefault(item => item.Name == name));
    }

    private void Keep(string contextId, string name, ContextEntry? entry)
    {
        var at = (contextId, name);
        if (entry is not null && entry.EffectiveTags.Contains(ContextTags.Interrupt) && entry.Provenance?.Trust is ContextTrust.User or ContextTrust.Platform)
        {
            _interrupts[at] = entry.Text;
        }
        else { _interrupts.Remove(at); }
        if (entry is not null && entry.EffectiveTags.Contains(ContextTags.UiFocus)) { _focus = entry; _focusAt = at; }
        else if (_focusAt == at) { _focus = null; _focusAt = null; }
    }

    // The first open approval Ino waits on: a step of one of its flows, or a staged synapse.
    private async Task ReadApprovals()
    {
        var pending = await Get<IApprovals>(IApprovals.Key(Id.Brain.Value)).Pending();
        _inboxOpen = pending.Count > 0;
        Wait? wait = null, current = null;
        foreach (var approval in pending)
        {
            if (approval.Kind == ApprovalKind.SynapseStaged) { wait ??= new Wait(null, null, null, approval.Title); continue; }
            if (approval.Kind != ApprovalKind.FlowStep) { continue; }
            var flow = await Get<IFlows>(IFlows.Key(Id.Brain.Value)).Read(approval.Subject);
            if (flow is null || !IsInoOwned(flow)) { continue; }
            var flowWait = new Wait(flow.FlowId, flow.WaitingStep ?? flow.Current, flow.Type, approval.Title);
            if (IsCurrentConversation(flow)) { current = flowWait; break; }
            wait ??= flowWait;
        }
        _wait = current ?? wait;
    }

    private async Task Recompute()
    {
        var now = Context.Time.GetUtcNow();
        var trace = _run?.TraceId ?? _lastTrace;
        var kind = trace is null ? null : await NeuronKindOn(trace);
        var focus = _focus is { Provenance: { } written } entry && written.At > _turnStarted ? PresenceText.FocusOf(entry) : null;
        PresenceState state;
        string? flowId = null, step = null, flowType = null, reason = null;
        if (_interrupts.Count > 0 || _blocked) { state = PresenceState.Paused; reason = _interrupts.Values.FirstOrDefault(); }
        else if (_wait is { } wait) { state = PresenceState.Waiting; (flowId, step, flowType, reason) = (wait.FlowId, wait.Step, wait.FlowType, wait.Reason); }
        else if (_run is { Tool: not null } tool)
        {
            state = IsSearch(tool) ? PresenceState.Searching : PresenceState.Working;
            reason = string.IsNullOrWhiteSpace(tool.Tool) ? null : tool.Tool;
        }
        else if (_run is { } run && now - run.Started < ListeningWindow) { state = PresenceState.Listening; }
        else if (_failure is { } failure && now - failure.At < AttentionWindow)
        {
            state = PresenceState.Attention;
            (flowId, flowType, reason) = (failure.FlowId, failure.FlowType, failure.Detail);
        }
        else { state = PresenceState.Idle; }
        Arm(state, now);
        var next = new Presence(state, flowId, step, flowType, kind, trace, focus, reason, State.Revision);
        if (next == State) { return; }
        var revision = State.Revision + 1;
        await Save(next with { Revision = revision }, new PresenceChanged(revision));
    }

    // Schedules a recompute for when the listening window, or a recent failure's attention window, closes.
    private void Arm(PresenceState state, DateTimeOffset now)
    {
        DateTimeOffset? closes = state == PresenceState.Listening && _run is { } run ? run.Started + ListeningWindow
            : _failure is { } failure && now - failure.At < AttentionWindow ? failure.At + AttentionWindow
            : null;
        if (closes is { } at) { Context.Timers.Start(WindowTimer, at - now > TimeSpan.Zero ? at - now : TimeSpan.FromMilliseconds(1), null); }
        else { Context.Timers.Stop(WindowTimer); }
    }

    // A search or read tool, by its name or, for a contract call, by the method it calls.
    private static bool IsSearch(Run run)
    {
        static bool Reads(string? name) => name is not null
            && (name.StartsWith("search", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("read", StringComparison.OrdinalIgnoreCase)
                || name.Contains("_search", StringComparison.OrdinalIgnoreCase)
                || name.Contains("_read", StringComparison.OrdinalIgnoreCase));
        if (Reads(run.Tool)) { return true; }
        if (string.IsNullOrEmpty(run.Arguments)) { return false; }
        try
        {
            using var document = JsonDocument.Parse(run.Arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String && Reads(method.GetString());
        }
        catch (JsonException) { return false; }
    }

    // The kind of the neuron that last published on the trace, other than this one and turn/conversation signals.
    private async Task<string?> NeuronKindOn(string traceId)
    {
        try
        {
            var page = await history.ReadTrace(traceId, 0, SignalPage.MaxLimit, CancellationToken.None);
            return page.Signals.LastOrDefault(item => item.Publisher != Id && !IsTurnSignal(item.Signal))?.Publisher.Type;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            PresenceLog.Unread(Logger(), error, "trace");
            return null;
        }
    }

    private static bool IsTurnSignal(Signal signal)
        => signal is ConversationChanged || Array.IndexOf(AgentSignals.All, signal.GetType()) >= 0;

    private async Task Quietly(Func<Task> read, string what)
    {
        try { await read(); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { PresenceLog.Unread(Logger(), error, what); }
    }

    private ILogger Logger() => (ILogger?)logger ?? OsConsoleLog.For<PresenceNeuron>();
}

internal static partial class PresenceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ino's presence could not read its {What}; it keeps what it had")]
    public static partial void Unread(ILogger logger, Exception exception, string what);
}
