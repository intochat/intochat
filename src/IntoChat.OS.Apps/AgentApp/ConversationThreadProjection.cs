using System.Text.Json;
using DigitalBrain.Log;
using DigitalBrain.UI;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.UI.Controls;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.AgentApp;

/// <summary>Optional caller-supplied hooks for customizing <see cref="ConversationThreadProjection.ToThreadMessages"/>.</summary>
public sealed record ConversationThreadOptions
{
    public Func<string?, (string Text, IReadOnlyList<Node> InlineCards)>? TransformAssistantText { get; init; }
    public Func<string, string?>? DescribeFailure { get; init; }
    public Func<Returned, string?, string, ActivityNode>? FormatActivity { get; init; }
    public Action<Returned, string?, List<Node>>? AppendStepCards { get; init; }
    public Action<string, IReadOnlyList<Returned>, IReadOnlyDictionary<(string RunId, string CallId), string>, List<Node>>? AppendRunCards { get; init; }
    public Action<Said, List<Node>>? AppendSaidCards { get; init; }
}

/// <summary>Optional inputs for projecting <see cref="ChatThreadProps"/> via <see cref="ConversationThreadProjection.ToThreadProps"/>.</summary>
public sealed record ConversationThreadPropsOptions
{
    public string Placeholder { get; init; } = "Message";
    public string? EmptyText { get; init; }
    public string? ConversationKey { get; init; }
    public string? SelectedModel { get; init; }
    public bool DeriveModelFromView { get; init; } = true;
    public bool? Working { get; init; }
    public bool? Blocked { get; init; }
    public bool HasPending { get; init; }
    public string? StatusOverride { get; init; }
    public string? ThreadFailure { get; init; }
    public ChatThreadError? Error { get; init; }
    public bool DeriveErrorFromView { get; init; } = true;
    public Func<string, string?>? DescribeFailure { get; init; }
    public IReadOnlyList<ThreadMessage>? ShownMessages { get; init; }
    public IReadOnlyList<ModelChoice>? Models { get; init; }
    public bool Dictation { get; init; }
    public bool ReadAloud { get; init; }
    public string? TalkConversation { get; init; }
    public string? ActiveContextLabel { get; init; }
    public Node? Waiting { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? AutomationName { get; init; }
}

/// <summary>
/// Pure projection and incremental view merge for <see cref="ConversationView"/> shared by
/// <c>InoApp</c> and runtime agent-app screens mounting <see cref="IChatThreadControl"/>.
/// </summary>
public static class ConversationThreadProjection
{
    public const int MaxRenderedMessages = ChatThreadProps.MaxDrawnMessages;
    public const int MaxRenderedChars = ChatThreadProps.MaxDrawnChars;

    /// <summary>Merges a full or incremental <see cref="ConversationView"/> read into the previously cached view.</summary>
    public static ConversationView MergeView(ConversationView? previous, ConversationView incoming, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (full || previous is null || !string.Equals(previous.Id.Value, incoming.Id.Value, StringComparison.Ordinal))
        {
            return incoming;
        }

        if (incoming.Entries.Length == 0)
        {
            return incoming with { Entries = previous.Entries };
        }

        var mergedEntries = new List<ConversationEntry>(previous.Entries.Length + incoming.Entries.Length);
        mergedEntries.AddRange(previous.Entries);
        var seen = new HashSet<EntryId>(previous.Entries.Select(static e => e.Id));
        foreach (var entry in incoming.Entries)
        {
            if (seen.Add(entry.Id))
            {
                mergedEntries.Add(entry);
            }
        }

        return incoming with { Entries = [.. mergedEntries] };
    }

    /// <summary>Returns the entry IDs superseded by a replacement reply or an in-flight regeneration.</summary>
    public static HashSet<EntryId> SupersededIds(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var superseded = new HashSet<EntryId>();
        foreach (var entry in view.Entries)
        {
            if (entry is Said { Replaces: { } target })
            {
                superseded.Add(target);
            }
        }

        if (view.Replacing is { } replacing)
        {
            superseded.Add(replacing);
        }

        return superseded;
    }

    /// <summary>Projects a <see cref="ConversationView"/> into ordered <see cref="ThreadMessage"/> items.</summary>
    public static IReadOnlyList<ThreadMessage> ToThreadMessages(
        ConversationView view,
        ConversationThreadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);

        var superseded = SupersededIds(view);
        var runsById = new Dictionary<string, AgentRun>(StringComparer.Ordinal);
        var toolArgs = new Dictionary<(string RunId, string CallId), string>();

        foreach (var run in view.Runs)
        {
            runsById[run.RunId] = run;
            foreach (var call in run.ToolCalls)
            {
                toolArgs[(run.RunId, call.CallId)] = call.Arguments;
            }
        }

        if (view.Active is { } activeRun)
        {
            runsById[activeRun.RunId] = activeRun;
            foreach (var call in activeRun.ToolCalls)
            {
                toolArgs[(activeRun.RunId, call.CallId)] = call.Arguments;
            }
        }

        var returnedByRun = new Dictionary<string, List<Returned>>(StringComparer.Ordinal);
        var replyRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in view.Entries)
        {
            switch (entry)
            {
                case Called called:
                    if (EntryIds.RunOf(called.Id) is { } callRun && EntryIds.CallOf(called.Id) is { } callId)
                    {
                        toolArgs[(callRun, callId)] = called.ArgsJson;
                    }
                    break;

                case Returned returned:
                    if (EntryIds.RunOf(returned.Id) is { } retRun)
                    {
                        if (!returnedByRun.TryGetValue(retRun, out var list))
                        {
                            list = [];
                            returnedByRun[retRun] = list;
                        }
                        list.Add(returned);
                    }
                    break;

                case Said { By: Participant.Assistant } agentSaid:
                    if (EntryIds.RunOf(agentSaid.Id) is { } repRun && agentSaid.Id == EntryIds.Reply(repRun))
                    {
                        replyRunIds.Add(repRun);
                    }
                    break;
            }
        }

        var emittedNonCompletedRuns = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<ThreadMessage>(view.Entries.Length + 2);

        foreach (var entry in view.Entries)
        {
            if (entry is not Said said)
            {
                continue;
            }

            if (said.By is Participant.Human human)
            {
                var sender = said.Sender ?? new SenderIdentity(human.Account.Value, human.Account.Value, SenderKind.Self);
                messages.Add(new ThreadMessage(said.Id.Value, "user", said.Text, "completed")
                {
                    Sender = sender,
                    Attachments = ProjectAttachments(said.Attachments),
                });

                if (EntryIds.RunOf(said.Id) is { } promptRunId
                    && !replyRunIds.Contains(promptRunId)
                    && !string.Equals(view.Active?.RunId, promptRunId, StringComparison.Ordinal)
                    && !superseded.Contains(EntryIds.Reply(promptRunId))
                    && runsById.TryGetValue(promptRunId, out var run)
                    && IsTerminalNonCompleted(run))
                {
                    emittedNonCompletedRuns.Add(promptRunId);
                    messages.Add(BuildNonCompletedReply(run, returnedByRun, toolArgs, options));
                }

                continue;
            }

            if (said.By is Participant.Assistant)
            {
                if (superseded.Contains(said.Id))
                {
                    continue;
                }

                messages.Add(BuildAgentReply(said, runsById, returnedByRun, toolArgs, options));
            }
        }

        // Any failed/cancelled/interrupted run without its own prompt entry (e.g., a failed regeneration of the latest turn).
        foreach (var run in view.Runs)
        {
            if (run.Posted
                || !IsTerminalNonCompleted(run)
                || emittedNonCompletedRuns.Contains(run.RunId)
                || replyRunIds.Contains(run.RunId)
                || string.Equals(view.Active?.RunId, run.RunId, StringComparison.Ordinal)
                || superseded.Contains(EntryIds.Reply(run.RunId)))
            {
                continue;
            }

            var bubble = BuildNonCompletedReply(run, returnedByRun, toolArgs, options);
            if (messages.Count > 0 && messages[^1] is { Role: "assistant", IsFailed: true })
            {
                messages[^1] = bubble;
            }
            else
            {
                messages.Add(bubble);
            }
        }

        if (view.Active is { } active && !replyRunIds.Contains(active.RunId))
        {
            messages.Add(BuildRunningReply(active, returnedByRun, toolArgs, options));
        }

        if (view.Active is null)
        {
            MarkLatestCompletedReplyRegenerable(messages);
        }

        return messages;
    }

    /// <summary>Caps projected messages to the tail message/character budget and appends an optimistic pending user prompt.</summary>
    public static IReadOnlyList<ThreadMessage> CapAndAppendPending(
        IReadOnlyList<ThreadMessage> source,
        string? pendingId = null,
        string? pendingText = null,
        int maxMessages = MaxRenderedMessages,
        int maxChars = MaxRenderedChars)
    {
        ArgumentNullException.ThrowIfNull(source);

        var selected = new List<ThreadMessage>(Math.Min(source.Count + 1, maxMessages + 1));
        var totalChars = 0;

        for (var i = source.Count - 1; i >= 0 && selected.Count < maxMessages; i--)
        {
            var msg = source[i];
            var text = msg.Text ?? string.Empty;
            if (selected.Count == 0 && text.Length > maxChars)
            {
                selected.Add(msg with { Text = text[..maxChars] });
                break;
            }

            if (totalChars + text.Length > maxChars)
            {
                break;
            }

            selected.Add(msg);
            totalChars += text.Length;
        }

        selected.Reverse();

        if (!string.IsNullOrEmpty(pendingId))
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (selected[i].CanRegenerate)
                {
                    selected[i] = selected[i] with { CanRegenerate = false };
                }
            }

            var rawPending = pendingText ?? string.Empty;
            var cappedPending = rawPending.Length > maxChars ? rawPending[..maxChars] : rawPending;
            selected.Add(new ThreadMessage($"pending/{pendingId}", "user", cappedPending, "sending"));
        }

        return selected;
    }

    /// <summary>Projects <see cref="ChatThreadProps"/> from a <see cref="ConversationView"/> and optional caller state.</summary>
    public static ChatThreadProps ToThreadProps(
        ConversationView? view,
        ConversationThreadPropsOptions? options = null)
    {
        var opts = options ?? new ConversationThreadPropsOptions();
        var shown = opts.ShownMessages ?? (view is not null ? ToThreadMessages(view) : []);
        var working = opts.Working ?? (view?.Active is not null);
        var blocked = opts.Blocked ?? view?.Blocked ?? false;
        var last = shown.Count > 0 ? shown[^1] : null;

        var error = opts.ThreadFailure is { Length: > 0 }
            ? new ChatThreadError(opts.ThreadFailure)
            : opts.Error is { } explicitError
                ? (explicitError.Retry && last is { Role: "assistant", IsFailed: true } ? null : explicitError)
                : opts.DeriveErrorFromView
                    ? DeriveErrorFromView(view, working, blocked, last, opts.DescribeFailure)
                    : null;

        var status = !string.IsNullOrWhiteSpace(opts.StatusOverride)
            ? opts.StatusOverride
            : blocked
                ? "paused"
                : working || opts.HasPending
                    ? "working"
                    : error is not null || last is { Role: "assistant", IsFailed: true }
                        ? "failed"
                        : "ready";

        var rawModel = opts.SelectedModel;
        if (string.IsNullOrWhiteSpace(rawModel) && opts.DeriveModelFromView && view is not null)
        {
            rawModel = LatestModelOf(view);
        }

        var activeModel = KnownModels.Find(rawModel);
        var activeModelId = activeModel?.Id.Value ?? (string.IsNullOrWhiteSpace(rawModel) ? null : rawModel.Trim());
        var activeModelLabel = activeModel?.Name ?? (string.IsNullOrWhiteSpace(rawModel) ? null : rawModel);

        return new ChatThreadProps(
            Placeholder: opts.Placeholder,
            CanSend: !working && !opts.HasPending && !blocked,
            ActiveModelLabel: activeModelLabel,
            ActiveProviderIcon: activeModel?.IconKey(),
            Dictation: opts.Dictation,
            Status: status,
            Working: working,
            EmptyText: shown.Count == 0 ? opts.EmptyText : null,
            Error: error,
            IsEnabled: opts.IsEnabled,
            AutomationName: opts.AutomationName,
            Models: opts.Models,
            ActiveModelId: activeModelId,
            Conversation: opts.ConversationKey ?? (view is not null ? IConversation.KeyOf(view.Id) : null),
            ReadAloud: opts.ReadAloud,
            TalkConversation: opts.TalkConversation,
            ActiveContextLabel: opts.ActiveContextLabel,
            Waiting: opts.Waiting);
    }

    /// <summary>Formats a raw model identifier or device-qualified model reference into display label and provider icon.</summary>
    public static (string? Label, string? ProviderIcon) FormatModelChip(string? rawModel)
    {
        if (string.IsNullOrWhiteSpace(rawModel))
        {
            return (null, null);
        }

        if (DeviceModelRef.TryParse(rawModel, out var deviceRef))
        {
            var inner = KnownModels.Find(deviceRef.Model.Value);
            return ($"{inner?.Name ?? deviceRef.Model.Value} · {deviceRef.Device}", inner?.IconKey() ?? "provider:device");
        }

        var card = KnownModels.Find(rawModel);
        return (card?.Name ?? rawModel, card?.IconKey());
    }

    /// <summary>Extracts a person-readable error string from a failed tool's JSON result payload.</summary>
    public static string ExtractErrorText(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return "Tool failed.";
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (GetString(doc.RootElement, "error") is { Length: > 0 } err) { return err; }
                if (GetString(doc.RootElement, "text") is { Length: > 0 } text) { return text; }
                if (GetString(doc.RootElement, "message") is { Length: > 0 } msg) { return msg; }
            }
        }
        catch (JsonException exception)
        {
            OsConsoleLog.For(typeof(ConversationThreadProjection)).LogSwallowed(exception, "Unreadable tool error payload");
        }

        return resultJson;
    }

    /// <summary>Default failure text for non-Ino agent screens when no custom failure descriptor is provided.</summary>
    public static string? DescribeFailureDefault(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return null;
        }

        return failureCode.Trim() switch
        {
            "cancelled" => "Stopped.",
            "interrupted" => "The run was interrupted.",
            "provider-unavailable" or "timed-out" => "The model provider did not answer.",
            "provider-rejected-key" => "The model provider refused the key.",
            "no-provider" => "No model is configured.",
            "budget" => "The monthly limit is reached.",
            "paused" => "Conversation is paused.",
            _ => ChatThreadProps.NoAnswer,
        };
    }

    private static bool IsTerminalNonCompleted(AgentRun run)
        => run.Status is AgentRunStatus.Failed or AgentRunStatus.Interrupted or AgentRunStatus.Cancelled;

    private static string FailureCodeOf(AgentRun run) => run.Status switch
    {
        AgentRunStatus.Cancelled => "cancelled",
        AgentRunStatus.Interrupted => "interrupted",
        _ => AgentFailures.CodeOf(run.Failure),
    };

    private static ChatThreadError? DeriveErrorFromView(
        ConversationView? view,
        bool working,
        bool blocked,
        ThreadMessage? last,
        Func<string, string?>? describeFailure)
    {
        if (blocked)
        {
            return new ChatThreadError("Conversation is paused.", view?.BlockedReason);
        }

        if (working || view is null || last is { Role: "assistant", IsFailed: true })
        {
            return null;
        }

        var superseded = SupersededIds(view);
        var latestRun = view.Runs.LastOrDefault(r => !r.Posted && !superseded.Contains(EntryIds.Reply(r.RunId)));
        if (latestRun is null || !IsTerminalNonCompleted(latestRun))
        {
            return null;
        }

        var code = FailureCodeOf(latestRun);
        var what = (describeFailure ?? DescribeFailureDefault)(code) ?? ChatThreadProps.NoAnswer;
        return new ChatThreadError(what, ActionLabel: "Try again", Retry: true);
    }

    private static string? LatestModelOf(ConversationView view)
    {
        if (view.Active?.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } activeModel)
        {
            return activeModel.Model;
        }

        for (var i = view.Runs.Length - 1; i >= 0; i--)
        {
            var run = view.Runs[i];
            if (!run.Posted && run.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } runModel)
            {
                return runModel.Model;
            }
        }

        for (var i = view.Entries.Length - 1; i >= 0; i--)
        {
            if (view.Entries[i] is Said { By: Participant.Assistant, Model: { Value.Length: > 0 } saidModel })
            {
                return saidModel.Value;
            }
        }

        return null;
    }

    private static void MarkLatestCompletedReplyRegenerable(List<ThreadMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var entryId = new EntryId(messages[i].Id);
            if (EntryIds.RunOf(entryId) is not { Length: > 0 } runId)
            {
                continue;
            }

            if (messages[i] is { Role: "assistant", Status: "completed" } && entryId == EntryIds.Reply(runId))
            {
                messages[i] = messages[i] with { CanRegenerate = true };
            }

            break;
        }
    }

    private static (string Text, IReadOnlyList<Node> InlineCards) TransformText(
        string? rawText,
        ConversationThreadOptions? options)
        => options?.TransformAssistantText is { } transform
            ? transform(rawText)
            : (rawText ?? string.Empty, Array.Empty<Node>());

    private static ThreadMessage BuildAgentReply(
        Said said,
        Dictionary<string, AgentRun> runsById,
        Dictionary<string, List<Returned>> returnedByRun,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        ConversationThreadOptions? options)
    {
        var runId = EntryIds.RunOf(said.Id);
        var (displayText, inlineSlots) = TransformText(said.Text, options);

        var rawModel = said.Model?.Value;
        if (string.IsNullOrWhiteSpace(rawModel) && runId is not null && runsById.TryGetValue(runId, out var run))
        {
            rawModel = run.Posted ? null : run.Model?.Model;
        }

        var (displayModel, providerIcon) = FormatModelChip(rawModel);

        var (activities, cards) = runId is not null && returnedByRun.TryGetValue(runId, out var returns)
            ? BuildStepVisuals(runId, returns, toolArgs, options)
            : (Array.Empty<ActivityNode>(), new List<Node>());

        options?.AppendSaidCards?.Invoke(said, cards);

        if (inlineSlots.Count > 0)
        {
            cards.AddRange(inlineSlots);
        }

        var agentSender = said.Sender
            ?? (said.By is Participant.Assistant assistant
                ? new SenderIdentity(assistant.AgentId, assistant.AgentId, SenderKind.Agent)
                : null);

        return new ThreadMessage(said.Id.Value, "assistant", displayText, "completed")
        {
            Model = displayModel,
            ProviderIcon = providerIcon,
            Activities = activities,
            Cards = cards,
            Sender = agentSender,
            Attachments = ProjectAttachments(said.Attachments),
        };
    }

    private static AttachmentNode[] ProjectAttachments(AttachmentRef[]? attachments)
    {
        if (attachments is not { Length: > 0 })
        {
            return [];
        }

        var nodes = new AttachmentNode[attachments.Length];
        for (var i = 0; i < attachments.Length; i++)
        {
            var a = attachments[i];
            var sizeBytes = (int)Math.Clamp(a.SizeBytes, 0, int.MaxValue);
            nodes[i] = new AttachmentNode(a.Id, a.Kind, a.FileName, a.MediaType, sizeBytes, a.Url, a.DurationMs, a.Transcript);
        }
        return nodes;
    }

    private static ThreadMessage BuildNonCompletedReply(
        AgentRun run,
        Dictionary<string, List<Returned>> returnedByRun,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        ConversationThreadOptions? options)
    {
        var (displayText, inlineSlots) = TransformText(run.Reply, options);
        var status = run.Status switch
        {
            AgentRunStatus.Cancelled => "cancelled",
            AgentRunStatus.Interrupted => "interrupted",
            _ => "failed",
        };
        var failureCode = FailureCodeOf(run);
        var describe = options?.DescribeFailure ?? DescribeFailureDefault;
        var failureText = describe(failureCode) ?? ChatThreadProps.NoAnswer;

        var rawModel = run.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } resolved ? resolved.Model : null;
        var (displayModel, providerIcon) = FormatModelChip(rawModel);

        var (activities, cards) = returnedByRun.TryGetValue(run.RunId, out var returns)
            ? BuildStepVisuals(run.RunId, returns, toolArgs, options)
            : (Array.Empty<ActivityNode>(), new List<Node>());

        if (inlineSlots.Count > 0)
        {
            cards.AddRange(inlineSlots);
        }

        return new ThreadMessage(EntryIds.Reply(run.RunId).Value, "assistant", displayText, status)
        {
            Model = displayModel,
            ProviderIcon = providerIcon,
            Failure = failureText,
            Activities = activities,
            Cards = cards,
        };
    }

    private static ThreadMessage BuildRunningReply(
        AgentRun active,
        Dictionary<string, List<Returned>> returnedByRun,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        ConversationThreadOptions? options)
    {
        var (displayText, inlineSlots) = TransformText(active.Reply, options);
        var rawModel = active.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } resolved ? resolved.Model : null;
        var (displayModel, providerIcon) = FormatModelChip(rawModel);

        var (activities, cards) = returnedByRun.TryGetValue(active.RunId, out var returns)
            ? BuildStepVisuals(active.RunId, returns, toolArgs, options)
            : (Array.Empty<ActivityNode>(), new List<Node>());

        if (inlineSlots.Count > 0)
        {
            cards.AddRange(inlineSlots);
        }

        return new ThreadMessage(EntryIds.Reply(active.RunId).Value, "assistant", displayText, "running")
        {
            Model = displayModel,
            ProviderIcon = providerIcon,
            Activities = activities,
            Cards = cards,
        };
    }

    private static (ActivityNode[] Activities, List<Node> Cards) BuildStepVisuals(
        string runId,
        List<Returned> returns,
        Dictionary<(string RunId, string CallId), string> toolArgs,
        ConversationThreadOptions? options)
    {
        var activities = new ActivityNode[returns.Count];
        var cards = new List<Node>();

        for (var i = 0; i < returns.Count; i++)
        {
            var r = returns[i];
            var callId = EntryIds.CallOf(r.Id) ?? r.Id.Value;
            toolArgs.TryGetValue((runId, callId), out var argsJson);

            activities[i] = options?.FormatActivity is { } formatActivity
                ? formatActivity(r, argsJson, callId)
                : DefaultActivityNode(r, callId);

            if (r.Ok)
            {
                if (r.Card is { Kind: "node", NodeJson.Length: > 0 } nodeCard)
                {
                    if (InoNodeCard.TryRead(nodeCard.NodeJson, out var parsedNode, out _))
                    {
                        cards.Add(Card(nodeCard.Title)[parsedNode]);
                    }
                }
                else
                {
                    options?.AppendStepCards?.Invoke(r, argsJson, cards);
                }
            }
        }

        options?.AppendRunCards?.Invoke(runId, returns, toolArgs, cards);

        return (activities, cards);
    }

    private static ActivityNode DefaultActivityNode(Returned returned, string callId)
    {
        var detail = returned.Ok
            ? (string.IsNullOrWhiteSpace(returned.ResultJson) ? null : returned.ResultJson)
            : ExtractErrorText(returned.ResultJson);
        var title = string.IsNullOrWhiteSpace(returned.Tool) ? "Tool call" : returned.Tool;
        return new ActivityNode(callId, title, returned.Ok ? "done" : "failed", detail, "capability:studio", "Tool");
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
