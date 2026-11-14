using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Apps.Ino;

public static class InoModel
{
    public const int MaxMessageChars = 32_000;
    public const string Ready = "Ready";
    public const string Working = "Working";
    public const string Failed = "Could not answer";
    public const string Paused = "Paused";
    public const string NewTitle = "New chat";
    public const string BlockedPrefix = "Blocked: ";
    public const string MigrationNote = "Earlier conversation is in the archive.";
    public const string MigrationId = "migration";

    public static string AgentKeyOf(string conversationId) => $"ino/conversations/{conversationId}";

    public static string ChatKeyOf(string conversationId) => $"ino/chats/{conversationId}";

    public static string ToneKeyOf(string conversationId) => $"observer/tone/{AgentKeyOf(conversationId)}";

    public static string MintConversationId() => ConversationId.New().Value;

    public static (InoAppState State, bool NeedsMigration) DecideOpen(
        InoAppState current,
        string? mode,
        string displayName,
        string defaultModel,
        bool smart,
        Approval[] approvals,
        string[] grantSummaries,
        string? detectedLocalModel = null,
        string[]? detectedLocalModels = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var status = mode == "greet"
            ? $"Hello, {displayName}! Ready on {defaultModel}."
            : current.Blocked ? Paused
            : current.ActiveRunId is not null ? Working
            : Ready;
        var migrate = current.Turns.Length > 0 && string.IsNullOrEmpty(current.AgentKey);
        var agentKey = AgentKeyOf(current.ConversationId);
        var next = current with
        {
            Status = status,
            AgentKey = migrate ? current.AgentKey : agentKey,
            Smart = smart,
            Approvals = approvals,
            GrantSummaries = grantSummaries,
            DetectedLocalModel = detectedLocalModel ?? current.DetectedLocalModel,
            DetectedLocalModels = detectedLocalModels ?? current.DetectedLocalModels,
        };
        return (next, migrate);
    }

    public static InoAppState Opened(
        InoAppState s,
        bool smart,
        Approval[] approvals,
        string[] grantSummaries,
        string? detectedLocalModel = null,
        string[]? detectedLocalModels = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            Smart = smart,
            Approvals = approvals,
            GrantSummaries = grantSummaries,
            DetectedLocalModel = detectedLocalModel ?? s.DetectedLocalModel,
            DetectedLocalModels = detectedLocalModels ?? s.DetectedLocalModels,
        };
    }

    public static string[] SummarizeGrants(IReadOnlyList<Grant> grants, string assistantAppId)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return
        [
            .. grants
                .Where(g => g.AppId == assistantAppId && !g.Revoked)
                .Select(g => $"{g.Scope} · {g.Target}"),
        ];
    }

    public static InoAppState Sent(InoAppState s, string text, string pendingId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(text);
        if (s.Pending is not null || s.ActiveRunId is not null)
        {
            return s;
        }

        if (text.Length > MaxMessageChars)
        {
            return s with
            {
                Error = new InoError("too-long"),
                Status = Failed,
            };
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return s;
        }

        var title = s.Title == NewTitle
            ? (trimmed.Length <= 52 ? trimmed : trimmed[..52])
            : s.Title;

        return s with
        {
            AgentKey = AgentKeyOf(s.ConversationId),
            Title = title,
            Pending = new InoPending(pendingId, trimmed, at),
            Error = null,
            Status = Working,
        };
    }

    public static InoAppState Started(InoAppState s, string runId)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            ActiveRunId = runId,
            Blocked = false,
            BlockedReason = null,
            Error = null,
            Status = Working,
        };
    }

    public static InoAppState StartFailed(InoAppState s, InoError error)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(error);
        var isPaused = string.Equals(error.Code, "paused", StringComparison.Ordinal);
        return s with
        {
            Pending = null,
            ActiveRunId = null,
            Blocked = isPaused || s.Blocked,
            BlockedReason = isPaused ? error.Detail : s.BlockedReason,
            Error = error,
            Status = isPaused ? Paused : Failed,
        };
    }

    public static InoAppState Projected(
        InoAppState s,
        ChatState chat,
        string fullAgentKey,
        IReadOnlyDictionary<string, AgentFailure>? runFailures = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(chat);
        if (chat.Revision <= s.ChatRevision || !string.Equals(chat.Selection?.Agent, fullAgentKey, StringComparison.Ordinal))
        {
            return s;
        }

        var entries = chat.Projection?.Messages ?? [];
        var messages = entries.Select(entry => ToMessage(entry, runFailures)).ToArray();

        var pending = s.Pending is { } p
            && messages.Any(m => m.Role == "user" && string.Equals(m.Text, p.Text, StringComparison.Ordinal))
            ? null
            : s.Pending;

        var isRunning = chat.Run?.Status == ChatRunStatus.Running;
        var activeRunId = isRunning ? chat.Run?.RunId : null;

        var lastAssistant = messages.LastOrDefault(m => m.Role == "assistant");
        InoError? error = null;
        var status = Ready;

        if (s.Blocked)
        {
            status = Paused;
            error = new InoError("paused", s.BlockedReason ?? "An interrupt holds the agent.");
        }
        else if (isRunning || pending is not null)
        {
            status = Working;
        }
        else if (lastAssistant is { Status: "failed" })
        {
            status = Failed;
            error = new InoError(lastAssistant.Failure ?? "unknown");
        }
        else if (lastAssistant is { Status: "interrupted" })
        {
            status = Failed;
            error = new InoError("interrupted");
        }
        else if (chat.Run?.Status == ChatRunStatus.Failed)
        {
            status = Failed;
            var failureCode = chat.Run.RunId is { } rid && runFailures is not null && runFailures.TryGetValue(rid, out var f)
                ? AgentFailures.CodeOf(f) ?? "unknown"
                : "unknown";
            error = new InoError(failureCode);
        }
        else if (chat.Run?.Status == ChatRunStatus.Interrupted)
        {
            status = Failed;
            error = new InoError("interrupted");
        }

        var toolCalls = entries
            .SelectMany(entry => entry.Activities.Select(a => new AgentToolCall(a.Id, a.Title, a.Input, a.Result, a.Error)));
        var projectedCards = InoCards.FromToolCalls(toolCalls);
        var preservedManualCards = s.ActiveCards
            .Where(existing => projectedCards.All(projected => !string.Equals(projected.Id, existing.Id, StringComparison.Ordinal)))
            .ToArray();
        var mergedCards = preservedManualCards.Length == 0
            ? projectedCards
            : [.. preservedManualCards, .. projectedCards];

        return s with
        {
            ChatRevision = chat.Revision,
            Messages = messages,
            Pending = pending,
            ActiveRunId = activeRunId,
            Status = status,
            Error = error,
            Cards = mergedCards,
        };
    }

    public static InoAppState Pause(InoAppState s, string? reason)
    {
        ArgumentNullException.ThrowIfNull(s);
        var cleanReason = string.IsNullOrWhiteSpace(reason)
            ? "An interrupt holds the agent."
            : reason.StartsWith(BlockedPrefix, StringComparison.Ordinal)
                ? reason[BlockedPrefix.Length..]
                : reason;
        return s with
        {
            Blocked = true,
            BlockedReason = cleanReason,
            ActiveRunId = null,
            Pending = null,
            Error = new InoError("paused", cleanReason),
            Status = Paused,
        };
    }

    public static InoAppState Resume(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            Blocked = false,
            BlockedReason = null,
            Error = s.Error?.Code == "paused" ? null : s.Error,
            Status = Ready,
        };
    }

    public static InoAppState BudgetExceeded(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            Pending = null,
            ActiveRunId = null,
            Error = new InoError("budget"),
            Status = "Monthly compute cap exceeded. Increase cap in Settings -> Billing (os://window/settings?section=billing).",
        };
    }

    public static InoAppState NewConversation(InoAppState s, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InoAppState(conversationId, NewTitle, Ready, "idle", [])
        {
            AgentKey = AgentKeyOf(conversationId),
            GrantSummaries = s.GrantSummaries,
            Smart = s.Smart,
            DetectedLocalModel = s.DetectedLocalModel,
            DetectedLocalModels = s.DetectedLocalModels,
        };
    }

    public static InoAppState DecideVoice(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with { VoiceStatus = "recording", Status = "Listening..." };
    }

    public static InoAppState DecideMake(InoAppState s, string target, string sentence, string brainId)
    {
        ArgumentNullException.ThrowIfNull(s);
        var cardId = $"app-{target}";
        var sourceNeuronUri = $"neuron://{brainId}/apps/{target}";
        var scopeUri = $"neuron://{brainId}/apps/ino#{cardId}";
        return s with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. s.ActiveCards, new InoCard(cardId, "applet", $"Live App: {target}", sentence, sourceNeuronUri, "running", scopeUri)],
        };
    }

    public static InoAppState DecideConnectDb(InoAppState s, string? newConnectedDb, string message, InoCard? card)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            ConnectedDatabase = newConnectedDb ?? s.ConnectedDatabase,
            Status = message,
            Cards = card is not null ? [.. s.ActiveCards, card] : s.Cards,
        };
    }

    public static InoAppState DecideQueryDbSuccess(InoAppState s, DataQueryResult result, string database)
    {
        ArgumentNullException.ThrowIfNull(s);
        var (table, chart) = InoDataCards.Build(result, database, $"table-{s.ActiveCards.Length + 1}", $"chart-{s.ActiveCards.Length + 2}");
        return s with
        {
            ConnectedDatabase = database,
            Status = $"Visualized {result.Rows.Length} row(s) & {result.ChartPoints.Length} chart point(s) from {database}",
            Cards = [.. s.ActiveCards, table, chart],
        };
    }

    private static InoMessage ToMessage(ChatEntry entry, IReadOnlyDictionary<string, AgentFailure>? runFailures)
    {
        var role = entry.Role == ChatRole.User ? "user" : "assistant";
        var entryCards = InoCards.FromToolCalls(
            entry.Activities.Select(a => new AgentToolCall(a.Id, a.Title, a.Input, a.Result, a.Error)));
        string? failure = null;
        if (role == "assistant" && string.Equals(entry.Status, "failed", StringComparison.Ordinal))
        {
            var runId = entry.Id.EndsWith("/assistant", StringComparison.Ordinal)
                ? entry.Id[..^"/assistant".Length]
                : entry.Id;
            failure = runFailures is not null
                && runFailures.TryGetValue(runId, out var runFailure)
                && runFailure != AgentFailure.Unknown
                ? AgentFailures.CodeOf(runFailure)
                : ClassifyErrorText(entry.Error);
        }

        return new InoMessage(entry.Id, role, entry.Text, entry.Status)
        {
            Model = entry.Model,
            Failure = failure,
            Activities = [.. entry.Activities.Select(a => new InoActivity(a.Id, a.Title, a.Result, a.Error))],
            Cards = entryCards,
        };
    }

    internal static string ClassifyErrorText(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return "unknown";
        }

        var trimmed = errorText.Trim();
        if (AgentFailures.ParseCode(trimmed) != AgentFailure.Unknown)
        {
            return trimmed;
        }

        if (trimmed.Contains("No model provider", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("No AI chat client", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("not configured", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Unconfigured", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Ollama endpoint is not configured", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("missing ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return "no-provider";
        }

        if (trimmed.Contains("401", StringComparison.Ordinal)
            || trimmed.Contains("403", StringComparison.Ordinal)
            || trimmed.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "provider-rejected-key";
        }

        if (trimmed.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "timed-out";
        }

        if (trimmed.Contains("502", StringComparison.Ordinal)
            || trimmed.Contains("503", StringComparison.Ordinal)
            || trimmed.Contains("504", StringComparison.Ordinal)
            || trimmed.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return "provider-unavailable";
        }

        return "unknown";
    }
}
