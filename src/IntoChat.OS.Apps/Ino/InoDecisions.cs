using DigitalBrain.Neurons;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Apps.Ino;

internal static class InoDecisions
{
    public const string BlockedPrefix = "Blocked: ";
    public const string NewTitle = "New chat";
    public const string MigrationNote = "Earlier conversation is in the archive.";
    public const string MigrationId = "migration";
    public const string SmartVariable = "ino.smart";
    public const string ApprovalsKey = "approvals/index";
    public const string DataKey = "ino/data";
    public const int SeenKept = 256;

    public static string AgentKeyOf(string conversationId) => $"ino/conversations/{conversationId}";

    public static string ToneKeyOf(string conversationId) => $"observer/tone/{AgentKeyOf(conversationId)}";

    public static string MintConversationId() => ConversationId.New().Value;

    public static (InoAppState State, bool NeedsMigration) DecideOpen(InoAppState current, string? mode, string displayName, string defaultModel)
    {
        var status = mode == "greet"
            ? $"Hello, {displayName}! Ready on {defaultModel}."
            : $"Ready ({defaultModel})";
        var migrate = current.Turns.Length > 0 && string.IsNullOrEmpty(current.AgentKey);
        var agentKey = AgentKeyOf(current.ConversationId);
        var next = current with { Status = status, AgentKey = migrate ? current.AgentKey : agentKey };
        return (next, migrate);
    }

    public static InoAppState DecideAgentRead(InoAppState current, bool blocked, string? existingBlockedReason)
    {
        return current with
        {
            Blocked = blocked,
            BlockedReason = blocked ? existingBlockedReason ?? "An interrupt holds the agent." : null
        };
    }

    public static InoAppState DecideGrants(InoAppState current, IReadOnlyList<Grant> grants, string assistantAppId)
    {
        var summaries = grants
            .Where(g => g.AppId == assistantAppId && !g.Revoked)
            .Select(g => $"{g.Scope} · {g.Target}")
            .ToArray();
        return current with { GrantSummaries = summaries };
    }

    public static InoAppState DecideBeforeSend(InoAppState current, string text)
    {
        var agentKey = AgentKeyOf(current.ConversationId);
        var title = current.Title == NewTitle ? (text.Length <= 52 ? text : text[..52]) : current.Title;
        return current with
        {
            AgentKey = agentKey,
            Title = title,
            Status = "Thinking"
        };
    }

    public static InoAppState DecideSendStarted(InoAppState current, string runId)
    {
        return current with
        {
            ActiveRunId = runId,
            Blocked = false,
            BlockedReason = null,
            Status = "Thinking"
        };
    }

    public static InoAppState DecideComputeBudgetExceeded(InoAppState current)
    {
        return current with
        {
            Status = "Monthly compute cap exceeded. Increase cap in Settings -> Billing (os://window/settings?section=billing).",
            ActiveRunId = null
        };
    }

    public static InoAppState DecideTurnProgressed(InoAppState current, TurnProgressed progressed)
    {
        var seen = $"{progressed.RunId}:{progressed.Sequence}";
        if (current.SeenProgress is not null && current.SeenProgress.Contains(seen, StringComparer.Ordinal))
        {
            return current;
        }

        var seenList = (current.SeenProgress ?? []).Append(seen).TakeLast(SeenKept).ToArray();
        var baseState = current with { SeenProgress = seenList };

        return progressed.Kind switch
        {
            "tool-call" => baseState with { Status = $"Using {progressed.Tool}" },
            "tool-result" when progressed.Payload is { Length: > 0 } payload
                => baseState with { Cards = [.. baseState.ActiveCards, .. InoCards.FromToolCalls([new AgentToolCall(seen, progressed.Tool ?? "", "", payload)])] },
            _ => baseState
        };
    }

    public static InoAppState DecideTurnCompleted(InoAppState current)
    {
        return current with { Status = "Ready", ActiveRunId = null };
    }

    public static InoAppState DecideTurnFailed(InoAppState current, TurnFailed failed)
    {
        if (failed.Error.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            return DecidePause(current, failed.Error) with { ActiveRunId = null };
        }
        return current with
        {
            ActiveRunId = null,
            Status = failed.Cancelled ? "Cancelled" : $"Failed: {failed.Error}"
        };
    }

    public static InoAppState DecideAgentBlocked(InoAppState current, AgentBlocked blocked)
    {
        return blocked.Blocked
            ? DecidePause(current, BlockedPrefix + "An interrupt holds the agent.")
            : current with { Blocked = false, BlockedReason = null, Status = "Ready" };
    }

    public static InoAppState DecidePause(InoAppState current, string message)
    {
        var reason = message.StartsWith(BlockedPrefix, StringComparison.Ordinal)
            ? message[BlockedPrefix.Length..]
            : message;
        return current with { Blocked = true, BlockedReason = reason, Status = "Paused" };
    }

    public static InoAppState DecideStop(InoAppState current)
    {
        return current with { Status = "Stopping" };
    }

    public static InoAppState DecideNew(InoAppState current, string newConversationId)
    {
        return new InoAppState(newConversationId, NewTitle, "New conversation started", "idle", [])
        {
            AgentKey = AgentKeyOf(newConversationId),
            GrantSummaries = current.GrantSummaries
        };
    }

    public static InoAppState DecideVoice(InoAppState current)
    {
        return current with { VoiceStatus = "recording", Status = "Listening..." };
    }

    public static InoAppState DecideMake(InoAppState current, string target, string sentence, string brainId)
    {
        var cardId = $"app-{target}";
        var sourceNeuronUri = $"neuron://{brainId}/apps/{target}";
        var scopeUri = $"neuron://{brainId}/apps/ino#{cardId}";
        return current with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. current.ActiveCards, new InoCard(cardId, "applet", $"Live App: {target}", sentence, sourceNeuronUri, "running", scopeUri)]
        };
    }

    public static InoAppState DecideConnectDb(InoAppState current, string? newConnectedDb, string message, InoCard? card)
    {
        return current with
        {
            ConnectedDatabase = newConnectedDb ?? current.ConnectedDatabase,
            Status = message,
            Cards = card is not null ? [.. current.ActiveCards, card] : current.Cards
        };
    }

    public static InoAppState DecideQueryDbSuccess(InoAppState current, DataQueryResult result, string database)
    {
        var (table, chart) = InoDataCards.Build(result, database, $"table-{current.ActiveCards.Length + 1}", $"chart-{current.ActiveCards.Length + 2}");
        return current with
        {
            ConnectedDatabase = database,
            Status = $"Visualized {result.Rows.Length} row(s) & {result.ChartPoints.Length} chart point(s) from {database}",
            Cards = [.. current.ActiveCards, table, chart]
        };
    }

    public static InoAppState DecideReconcile(InoAppState current, AgentRun? activeRun, IReadOnlyList<AgentRun> runs, string? runId, out bool reloadTranscript)
    {
        reloadTranscript = false;
        if (runId is null)
        {
            return activeRun is { } running ? current with { ActiveRunId = running.RunId, Status = "Thinking" } : current;
        }
        if (activeRun?.RunId == runId)
        {
            return current;
        }
        if (runs.FirstOrDefault(run => run.RunId == runId) is not { } ended)
        {
            var fallbackStatus = current.Status == "Thinking" || current.Status.StartsWith("Using ", StringComparison.Ordinal) ? "Ready" : current.Status;
            return current with { ActiveRunId = null, Status = fallbackStatus };
        }
        reloadTranscript = true;
        var endStatus = ended.Status switch
        {
            AgentRunStatus.Completed => "Ready",
            AgentRunStatus.Cancelled => "Cancelled",
            AgentRunStatus.Running => current.Status,
            _ => $"Failed: {ended.Error}"
        };
        return current with { ActiveRunId = null, Status = endStatus };
    }
}
