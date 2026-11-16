using System.Text.Json;
using DigitalBrain.Rules;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Primitives;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Ino;

/// <summary>
/// Rich DDD Aggregate representing an Ino conversation session, its invariants, rules, and pure commands.
/// Unfoldable via <see cref="AggregateExplorer{TState}"/> and convertible 1:1 with <see cref="InoAppState"/>.
/// </summary>
public sealed record InoSession(
    string ConversationId = "main",
    string Title = InoModel.NewTitle,
    string Status = InoModel.Ready,
    bool IsPinned = true,
    string? PendingPrompt = null,
    string? ActiveRunId = null,
    bool Blocked = false,
    string? BlockedReason = null,
    string? ErrorCode = null,
    string? LastUserPrompt = null,
    string? LastReply = null,
    string? SelectedModel = null,
    string? DetectedLocalModel = "qwen2.5-coder:14b")
{
    public InoSession() : this("main") { }

    [Invariant("Session title can never be blank.")]
    public bool NonEmptyTitle => !string.IsNullOrWhiteSpace(Title);

    [Invariant]
    public Rule BlockedImpliesPaused => Decision.Require(
        "blocked_is_paused",
        !Blocked || (Status == InoModel.Paused && PendingPrompt is null && ActiveRunId is null),
        "A blocked session must be paused with no active or pending turn.");

    [Invariant]
    public Rule ActiveOrPendingImpliesWorking => Decision.Require(
        "busy_is_working",
        (PendingPrompt is null && ActiveRunId is null) || Status == InoModel.Working,
        "A session with a pending prompt or active run must report Working status.");

    public Rule MustBeIdle => InoModel.IdleRule(PendingPrompt is not null, ActiveRunId is not null);
    public Rule MustNotBeBlocked => InoModel.NotBlockedRule(Blocked);
    public Rule HasActiveTurn => Decision.Rule("has_active_turn", PendingPrompt is null && ActiveRunId is null, "No turn is currently running.");
    public Rule CanRetry => Decision.Rule("can_retry", PendingPrompt is not null || ActiveRunId is not null || string.IsNullOrWhiteSpace(LastUserPrompt), "Nothing to retry while idle without a prior prompt.");

    public Decision<InoSession> Send([Sample("Summarize Q4 renewals", "")] string prompt) =>
        Decision.Check("send", MustNotBeBlocked, MustBeIdle, InoModel.NonEmptyPromptRule(prompt), InoModel.WithinLimitRule(prompt))
        ?? Decision.Accept(this with
        {
            Title = Title == InoModel.NewTitle ? InoModel.DeriveTitle(prompt.Trim()) : Title,
            PendingPrompt = prompt.Trim(),
            LastUserPrompt = prompt.Trim(),
            ErrorCode = null,
            Status = InoModel.Working,
        });

    public Decision<InoSession> StartRun([Sample("run-1")] string runId) =>
        Decision.Check("start-run", Decision.Rule("pending_required", PendingPrompt is null, "Pending prompt required."), Decision.Rule("run_not_started", ActiveRunId is not null, "Run already active."))
        ?? Decision.Accept(this with { ActiveRunId = runId, Blocked = false, BlockedReason = null, ErrorCode = null, Status = InoModel.Working });

    public Decision<InoSession> CompleteTurn([Sample("All 3 renewals are healthy.")] string reply) =>
        Decision.Check("complete-turn", HasActiveTurn)
        ?? Decision.Accept(this with { PendingPrompt = null, ActiveRunId = null, ErrorCode = null, LastReply = reply, Status = InoModel.Ready });

    public Decision<InoSession> FailTurn([Sample("no-provider")] string code) =>
        Decision.Check("fail-turn", HasActiveTurn)
        ?? Decision.Accept(this with { PendingPrompt = null, ActiveRunId = null, ErrorCode = string.IsNullOrWhiteSpace(code) ? "unknown" : code, Status = InoModel.Failed });

    public Decision<InoSession> Pause([Sample("Approval required")] string reason) =>
        Decision.Check("pause", Decision.Rule("already_blocked", Blocked, "Session is already paused."))
        ?? Decision.Accept(this with { Blocked = true, BlockedReason = InoModel.NormalizeBlockReason(reason), PendingPrompt = null, ActiveRunId = null, ErrorCode = "paused", Status = InoModel.Paused });

    public Decision<InoSession> Resume() =>
        Decision.Check("resume", Decision.Rule("not_paused", !Blocked, "Session is not paused."))
        ?? Decision.Accept(this with { Blocked = false, BlockedReason = null, ErrorCode = ErrorCode == "paused" ? null : ErrorCode, Status = InoModel.Ready });

    public Decision<InoSession> Retry() =>
        Decision.Check("retry", MustNotBeBlocked, CanRetry)
        ?? Decision.Accept(this with { PendingPrompt = LastUserPrompt, ErrorCode = null, Status = InoModel.Working });

    public Decision<InoSession> SelectModel([Sample("qwen2.5-coder:14b")] string model) =>
        Decision.Check("select-model", Decision.Rule("model_required", string.IsNullOrWhiteSpace(model), "Model identifier is required."))
        ?? Decision.Accept(this with
        {
            SelectedModel = model.Trim(),
            ErrorCode = ErrorCode is "no-provider" or "provider-rejected-key" ? null : ErrorCode,
            Status = ActiveRunId is not null || PendingPrompt is not null ? InoModel.Working : Blocked ? InoModel.Paused : InoModel.Ready,
        });

    public InoSession TogglePin() => this with { IsPinned = !IsPinned };

    [IgnoreExplore]
    public InoAppState ToAppState()
    {
        var messages = new List<InoMessage>(2);
        if (!string.IsNullOrEmpty(LastUserPrompt) && PendingPrompt is null)
        {
            messages.Add(new InoMessage("r1/user", "user", LastUserPrompt));
            if (LastReply is not null) { messages.Add(new InoMessage("r1/assistant", "assistant", LastReply) { Model = SelectedModel }); }
            else if (ErrorCode is not null and not "paused") { messages.Add(new InoMessage("r1/assistant", "assistant", string.Empty, "failed") { Failure = ErrorCode }); }
        }

        return new InoAppState(ConversationId, Title, Status, "idle", [], IsPinned: IsPinned, AgentKey: InoModel.AgentKeyOf(ConversationId), ActiveRunId: ActiveRunId, Blocked: Blocked, BlockedReason: BlockedReason)
        {
            Messages = messages.Count > 0 ? [.. messages] : null,
            Pending = PendingPrompt is { } p ? new InoPending("p1", p, DateTimeOffset.UnixEpoch) : null,
            Error = ErrorCode is { } code ? new InoError(code, code == "paused" ? BlockedReason : null) : null,
            SelectedModel = SelectedModel,
            DetectedLocalModel = DetectedLocalModel,
            DetectedLocalModels = DetectedLocalModel is { } m ? [m] : null,
        };
    }

    public static InoSession FromState(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InoSession(
            s.ConversationId,
            s.Title,
            s.Status,
            s.IsPinned,
            s.Pending?.Text,
            s.ActiveRunId,
            s.Blocked,
            s.BlockedReason,
            s.Error?.Code,
            s.Pending?.Text ?? (s.Messages ?? []).LastOrDefault(m => m.Role == "user")?.Text,
            (s.Messages ?? []).LastOrDefault(m => m.Role == "assistant" && m.Status == "completed")?.Text,
            s.SelectedModel,
            s.DetectedLocalModel);
    }

    public override string ToString() => $"{Status} · {Title}" + (ErrorCode is { } e ? $" [{e}]" : "");
}

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

    public static Rule IdleRule(bool hasPending, bool hasActiveRun)
        => Decision.Rule("not_busy", hasPending || hasActiveRun, "Wait for the current reply to finish or stop it first.");

    public static Rule NotBlockedRule(bool blocked)
        => Decision.Rule("not_blocked", blocked, "Ino is paused until the pending interrupt is resolved.");

    public static Rule NonEmptyPromptRule(string? text)
        => Decision.Rule("non_empty_prompt", string.IsNullOrWhiteSpace(text), "Enter a message to send.");

    public static Rule WithinLimitRule(string? text)
        => Decision.Rule("within_max_chars", (text?.Length ?? 0) > MaxMessageChars, $"Messages cannot exceed {MaxMessageChars:N0} characters.");

    public static Decision<InoAppState> DecideSend(InoAppState s, string? text, string pendingId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(s);
        var refusal = Decision.Check("send", IdleRule(s.Pending is not null, s.ActiveRunId is not null), NonEmptyPromptRule(text), WithinLimitRule(text));
        if (refusal is not null)
        {
            return refusal.Rejection.Rules.Contains("within_max_chars", StringComparer.Ordinal)
                && !refusal.Rejection.Rules.Contains("not_busy", StringComparer.Ordinal)
                ? Decision.Accept(s with { Error = new InoError("too-long"), Status = Failed })
                : refusal;
        }

        var trimmed = text!.Trim();
        return Decision.Accept(s with
        {
            AgentKey = AgentKeyOf(s.ConversationId),
            Title = s.Title == NewTitle ? DeriveTitle(trimmed) : s.Title,
            Pending = new InoPending(pendingId, trimmed, at),
            Error = null,
            Status = Working,
        });
    }

    public static Decision<string> DecideRetryPrompt(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lastUser = (s.Messages ?? []).LastOrDefault(m => m.Role == "user")?.Text;
        var refusal = Decision.Check("retry", IdleRule(s.Pending is not null, s.ActiveRunId is not null), Decision.Rule("has_prior_prompt", string.IsNullOrWhiteSpace(lastUser), "No prior message to try again."));
        return refusal is not null ? refusal : Decision.Accept(lastUser!);
    }

    internal static string DeriveTitle(string trimmed) => trimmed.Length <= 52 ? trimmed : trimmed[..52];

    internal static string NormalizeBlockReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "An interrupt holds the agent."
            : reason.StartsWith(BlockedPrefix, StringComparison.Ordinal) ? reason[BlockedPrefix.Length..]
            : reason;

    public static (InoAppState State, bool NeedsMigration) DecideOpen(
        InoAppState current, string? mode, string displayName, string? defaultModel, bool smart,
        Approval[] approvals, string[] grantSummaries, string? detectedLocalModel = null, string[]? detectedLocalModels = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var modelLabel = string.IsNullOrWhiteSpace(defaultModel) ? ModelCatalog.DefaultChatModelId : defaultModel.Trim();
        var status = mode == "greet" ? $"Hello, {displayName}! Ready on {modelLabel}."
            : current.Blocked ? Paused : current.ActiveRunId is not null ? Working : Ready;
        var migrate = current.Turns.Length > 0 && string.IsNullOrEmpty(current.AgentKey);
        var next = Opened(current, smart, approvals, grantSummaries, detectedLocalModel, detectedLocalModels) with
        {
            Status = status,
            AgentKey = migrate ? current.AgentKey : AgentKeyOf(current.ConversationId),
            SelectedModel = modelLabel,
        };
        return (next, migrate);
    }

    public static InoAppState Opened(
        InoAppState s, bool smart, Approval[] approvals, string[] grantSummaries,
        string? detectedLocalModel = null, string[]? detectedLocalModels = null)
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
        return [.. grants.Where(g => g.AppId == assistantAppId && !g.Revoked).Select(g => $"{g.Scope} · {g.Target}")];
    }

    public static InoAppState Sent(InoAppState s, string text, string pendingId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(text);
        return DecideSend(s, text, pendingId, at) is { IsAccepted: true, Next: var next } ? next : s;
    }

    public static InoAppState Started(InoAppState s, string runId, string? selectedModel = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with { ActiveRunId = runId, Blocked = false, BlockedReason = null, Error = null, Status = Working, SelectedModel = selectedModel ?? s.SelectedModel };
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

    public static InoAppState Projected(InoAppState s, ChatState chat, string fullAgentKey)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(chat);
        if (chat.Revision <= s.ChatRevision || !string.Equals(chat.Selection?.Agent, fullAgentKey, StringComparison.Ordinal))
        {
            return s;
        }

        var entries = chat.Projection?.Messages ?? [];
        var messages = entries.Select(ToMessage).ToArray();
        var isRunning = chat.Run?.Status == ChatRunStatus.Running;
        var priorMatches = s.Pending is { } prior ? CountUserMatches(s.Messages, prior.Text) : 0;
        var currentMatches = s.Pending is { } curr ? CountUserMatches(messages, curr.Text) : 0;
        var pending = s.Pending is not null && currentMatches > priorMatches ? null : s.Pending;
        var activeRunId = isRunning ? chat.Run?.RunId
            : s.ActiveRunId is { } started && !string.Equals(chat.Run?.RunId, started, StringComparison.Ordinal) ? started : null;
        var (status, error) = ResolveProjectedStatus(s, chat.Run, messages, isRunning || pending is not null || activeRunId is not null);

        var activeTurnModel = entries.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Model))?.Model;
        var selectedModel = !string.IsNullOrWhiteSpace(activeTurnModel)
            && !string.Equals(activeTurnModel, "default", StringComparison.OrdinalIgnoreCase)
            ? activeTurnModel
            : s.SelectedModel;

        var projectedCards = InoCards.FromToolCalls(entries.SelectMany(e => e.Activities.Select(a => new AgentToolCall(a.Id, a.Title, a.Input, a.Result, a.Error))));
        var manualCards = s.ActiveCards.Where(existing => projectedCards.All(p => !string.Equals(p.Id, existing.Id, StringComparison.Ordinal))).ToArray();

        return s with
        {
            ChatRevision = chat.Revision,
            Messages = messages,
            Pending = pending,
            ActiveRunId = activeRunId,
            Status = status,
            Error = error,
            SelectedModel = selectedModel,
            Cards = manualCards.Length == 0 ? projectedCards : [.. manualCards, .. projectedCards],
        };
    }

    private static int CountUserMatches(IReadOnlyList<InoMessage>? messages, string text)
        => (messages ?? []).Count(m => m.Role == "user" && string.Equals(m.Text, text, StringComparison.Ordinal));

    private static (string Status, InoError? Error) ResolveProjectedStatus(
        InoAppState s, ChatRunSnapshot? run, InoMessage[] messages, bool busy)
    {
        if (s.Blocked) { return (Paused, new InoError("paused", s.BlockedReason ?? "An interrupt holds the agent.")); }
        if (busy) { return (Working, null); }
        var lastAssistant = messages.LastOrDefault(m => m.Role == "assistant");
        if (lastAssistant is { Status: "failed" }) { return (Failed, new InoError(lastAssistant.Failure ?? "unknown")); }
        if (lastAssistant is { Status: "interrupted" } || run?.Status == ChatRunStatus.Interrupted) { return (Failed, new InoError("interrupted")); }
        if (run?.Status == ChatRunStatus.Failed) { return (Failed, new InoError("unknown")); }

        return (Ready, null);
    }

    public static InoAppState Pause(InoAppState s, string? reason)
    {
        ArgumentNullException.ThrowIfNull(s);
        var clean = NormalizeBlockReason(reason);
        return s with { Blocked = true, BlockedReason = clean, ActiveRunId = null, Pending = null, Error = new InoError("paused", clean), Status = Paused };
    }

    public static InoAppState Resume(InoAppState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with { Blocked = false, BlockedReason = null, Error = s.Error?.Code == "paused" ? null : s.Error, Status = Ready };
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
            SelectedModel = s.SelectedModel,
            DetectedLocalModel = s.DetectedLocalModel,
            DetectedLocalModels = s.DetectedLocalModels,
        };
    }

    public static InoAppState ModelSelected(InoAppState s, string model, string status, LocalModelSummary? local = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            Error = null,
            Status = status,
            SelectedModel = model,
            DetectedLocalModel = local?.PreferredModel ?? (local is null ? s.DetectedLocalModel : model),
            DetectedLocalModels = local?.InstalledModels ?? s.DetectedLocalModels,
        };
    }

    public static InoAppState SyncedSettings(InoAppState s, bool smart, string? defaultModel, string? displayName, string? changedModel)
    {
        ArgumentNullException.ThrowIfNull(s);
        var nextModel = string.IsNullOrWhiteSpace(defaultModel) ? s.SelectedModel : defaultModel.Trim();
        return s.Status.StartsWith("Switched model to ", StringComparison.Ordinal)
            ? s with { Smart = smart, SelectedModel = nextModel }
            : s with { Smart = smart, SelectedModel = nextModel, Status = $"Synced settings: {displayName} ({changedModel})" };
    }

    public static InoAppState DecideSlashPin(InoAppState s)
        => s with { IsPinned = !s.IsPinned, Status = s.IsPinned ? "Unpinned session" : "Pinned session to Arc bar" };

    public static InoAppState DecideSlashRename(InoAppState s, string? arg)
    {
        var nextTitle = string.IsNullOrWhiteSpace(arg) ? "Renamed session" : arg;
        return s with { Title = nextTitle, Status = $"Renamed to '{nextTitle}'" };
    }

    public static InoAppState DecideSlashHelp(InoAppState s, string rawCommand, string cmd)
        => s with { Status = $"Executed /{cmd}", Turns = [.. s.Turns, new InoTurn(rawCommand, "Commands: `/new`, `/rename <title>`, `/pin`, `/model <id>`, `/clear`.", $"slash:{cmd}", 0)] };

    public static InoAppState DecideScenario(string? scenarioId, UserSettingsState settings)
    {
        var model = string.IsNullOrWhiteSpace(settings.DefaultModel) ? ModelCatalog.DefaultChatModelId : settings.DefaultModel.Trim();
        var id = string.IsNullOrWhiteSpace(scenarioId) ? "q4-renewal" : scenarioId.Trim().ToLowerInvariant();
        if (id == "morning-brief")
        {
            return new(id, "Morning Brief", $"Scenario: Morning Brief ({model})", "idle",
                [new InoTurn("Prepare my Morning Brief", "Good morning! You have **3 meetings**, **2 Q4 renewals**, and **1 pending review**.", "scenario:morning-brief", 2)],
                [new InoCard("choice-brief", "choice", "Which workspace should receive the Q4 renewal brief?", "Select a target Circle (1..2) or press Enter.", "neuron://local/circles/primary", "pending", "neuron://local/apps/ino#choice-brief")]);
        }

        if (id.StartsWith("invoice", StringComparison.Ordinal) || id.Contains("vault", StringComparison.Ordinal) || id.Contains("tripletex", StringComparison.Ordinal))
        {
            return new(id, "Tripletex Invoice Check", "Waiting on Secure Vault credential", "idle",
                [new InoTurn("Check overdue Q4 invoices in Tripletex", "I need your **Tripletex Session Token** to read the Q4 ledger. Enter it in the **Secure Vault** window so it never touches the LLM.", "vault:secret", 0)],
                [new InoCard("secret-tripletex", "secret", "Tripletex Session Token", "Zero-leakage credential request · Sealed in Secure Vault", "neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN", "pending", "neuron://local/apps/ino#secret-tripletex")]);
        }

        if (id == "first-run")
        {
            return new(id, "Ino Setup · First Run", "Guided onboarding · 3 steps", "idle",
                [new InoTurn("/scenario first-run", $"Welcome to **IntoChat OS**! Let's verify your default AI model (`{model}`), connect your **Files** vault, and seal API keys in **Secure**.", "onboarding:setup", 0)],
                [new InoCard("setup-record", "record", "IntoChat OS First-Run Checklist", "All 3 local brain capabilities online", "neuron://local/os.settings/settings", "ready", "neuron://local/apps/ino#setup-record")]);
        }

        return new("q4-renewal", "Quarterly Enterprise Renewal", "Waiting for approval · CloseDate -> 15 Nov 2026", "idle",
            [new InoTurn("Update quarterly enterprise renewal close date to 15 Nov 2026", "Confirmed moving the **Quarterly Enterprise** renewal to **15 Nov 2026**. Please approve the CRM update below.", $"agent:{model}", 1)],
            [
                new InoCard("card-1", "approval", "Update close date on quarterly renewal to 15 Nov 2026?", "Ino matched the incoming email to your open CRM opportunity.", "neuron://local/os.confirmations/inbox", "pending", "neuron://local/apps/ino#card-1"),
                new InoCard("draft-1", "draft", "Quarterly Enterprise Renewal — 15 Nov confirmation", "Confirmed: we updated the Q4 renewal close date to 15 Nov 2026.", "neuron://local/apps/ino#draft-1", "pending", "neuron://local/apps/ino#draft-1"),
            ]);
    }

    public static InoAppState DecideMake(InoAppState s, string target, string sentence, string brainId)
    {
        ArgumentNullException.ThrowIfNull(s);
        var cardId = $"app-{target}";
        return s with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. s.ActiveCards, new InoCard(cardId, "applet", $"Live App: {target}", sentence, $"neuron://{brainId}/apps/{target}", "running", $"neuron://{brainId}/apps/ino#{cardId}")],
        };
    }

    public static InoAppState DecideConnectDb(InoAppState s, string? newConnectedDb, string message, InoCard? card)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with { ConnectedDatabase = newConnectedDb ?? s.ConnectedDatabase, Status = message, Cards = card is not null ? [.. s.ActiveCards, card] : s.Cards };
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

    private static InoMessage ToMessage(ChatEntry entry)
    {
        var role = entry.Role == ChatRole.User ? "user" : "assistant";
        var entryCards = InoCards.FromToolCalls(entry.Activities.Select(a => new AgentToolCall(a.Id, a.Title, a.Input, a.Result, a.Error)));
        var failure = role == "assistant" && string.Equals(entry.Status, "failed", StringComparison.Ordinal)
            ? entry.Failure ?? "unknown"
            : null;

        return new InoMessage(entry.Id, role, entry.Text, entry.Status)
        {
            Model = entry.Model,
            Failure = failure,
            Activities = [.. entry.Activities.Select(a => new InoActivity(a.Id, FormatActivityTitle(a.Title, a.Input), a.Result, a.Error))],
            Cards = entryCards,
        };
    }

    internal static string FormatActivityTitle(string? title, string? input)
    {
        if (string.IsNullOrWhiteSpace(title)) { return string.Empty; }
        var trimmed = title.Trim();
        if (trimmed is not ("neurons_call" or "neurons_get" or "neurons_list")
            || string.IsNullOrWhiteSpace(input)
            || !input.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return trimmed;
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement is not { ValueKind: JsonValueKind.Object } root) { return trimmed; }
            var contract = root.TryGetProperty("contract", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var useKey = !string.IsNullOrWhiteSpace(key)
                && (string.IsNullOrWhiteSpace(contract)
                    || contract.EndsWith(".IArtCraftStudio", StringComparison.Ordinal) || contract.Equals("IArtCraftStudio", StringComparison.OrdinalIgnoreCase)
                    || contract.EndsWith(".IApp", StringComparison.Ordinal) || contract.Equals("IApp", StringComparison.OrdinalIgnoreCase));
            var target = useKey ? key : !string.IsNullOrWhiteSpace(contract) ? contract : key;
            if (string.IsNullOrWhiteSpace(target)) { return trimmed; }
            var combined = !string.IsNullOrWhiteSpace(method) && !string.Equals(method, "Request", StringComparison.OrdinalIgnoreCase) ? $"{target}.{method}" : target;
            return InoView.ResolveActivityVisual(new InoActivity(string.Empty, combined)).Label;
        }
        catch (JsonException) { return trimmed; }
    }
}
