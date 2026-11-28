using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Rules;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
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
        this,
        static s => !s.Blocked || (s.Status == InoModel.Paused && s.PendingPrompt is null && s.ActiveRunId is null),
        "A blocked session must be paused with no active or pending turn.");

    [Invariant]
    public Rule ActiveOrPendingImpliesWorking => Decision.Require(
        "busy_is_working",
        this,
        static s => (s.PendingPrompt is null && s.ActiveRunId is null) || s.Status == InoModel.Working,
        "A session with a pending prompt or active run must report Working status.");

    public Rule MustBeIdle => InoModel.IdleRule(this);
    public Rule MustNotBeBlocked => InoModel.NotBlockedRule(this);
    public Rule HasActiveTurn => Decision.Rule("has_active_turn", this, static s => s.PendingPrompt is null && s.ActiveRunId is null, "No turn is currently running.");
    public Rule CanRetry => Decision.Rule("can_retry", this, static s => s.PendingPrompt is not null || s.ActiveRunId is not null || string.IsNullOrWhiteSpace(s.LastUserPrompt), "Nothing to retry while idle without a prior prompt.");

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
        Decision.Check(
            "start-run",
            Decision.Rule("pending_required", this, static s => s.PendingPrompt is null, "Pending prompt required."),
            Decision.Rule("run_not_started", this, static s => s.ActiveRunId is not null, "Run already active."))
        ?? Decision.Accept(this with { ActiveRunId = runId, Blocked = false, BlockedReason = null, ErrorCode = null, Status = InoModel.Working });

    public Decision<InoSession> CompleteTurn([Sample("All 3 renewals are healthy.")] string reply) =>
        Decision.Check("complete-turn", HasActiveTurn)
        ?? Decision.Accept(this with { PendingPrompt = null, ActiveRunId = null, ErrorCode = null, LastReply = reply, Status = InoModel.Ready });

    public Decision<InoSession> FailTurn([Sample("no-provider")] string code) =>
        Decision.Check("fail-turn", HasActiveTurn)
        ?? Decision.Accept(this with { PendingPrompt = null, ActiveRunId = null, ErrorCode = string.IsNullOrWhiteSpace(code) ? "unknown" : code, Status = InoModel.Failed });

    public Decision<InoSession> Pause([Sample("Approval required")] string reason) =>
        Decision.Check("pause", Decision.Rule("already_blocked", this, static s => s.Blocked, "Session is already paused."))
        ?? Decision.Accept(this with { Blocked = true, BlockedReason = InoModel.NormalizeBlockReason(reason), PendingPrompt = null, ActiveRunId = null, ErrorCode = "paused", Status = InoModel.Paused });

    public Decision<InoSession> Resume() =>
        Decision.Check("resume", Decision.Rule("not_paused", this, static s => !s.Blocked, "Session is not paused."))
        ?? Decision.Accept(this with { Blocked = false, BlockedReason = null, ErrorCode = ErrorCode == "paused" ? null : ErrorCode, Status = InoModel.Ready });

    public Decision<InoSession> Retry() =>
        Decision.Check("retry", MustNotBeBlocked, CanRetry)
        ?? Decision.Accept(this with { PendingPrompt = LastUserPrompt, ErrorCode = null, Status = InoModel.Working });

    public Decision<InoSession> SelectModel([Sample("qwen2.5-coder:14b")] string model) =>
        Decision.Check("select-model", Decision.Rule("model_required", model, static m => string.IsNullOrWhiteSpace(m), "Model identifier is required."))
        ?? Decision.Accept(this with
        {
            SelectedModel = model.Trim(),
            ErrorCode = ErrorCode is "no-provider" or "provider-rejected-key" ? null : ErrorCode,
            Status = ActiveRunId is not null || PendingPrompt is not null ? InoModel.Working : Blocked ? InoModel.Paused : InoModel.Ready,
        });

    public InoSession TogglePin() => this with { IsPinned = !IsPinned };

    [IgnoreExplore]
    public InoAppState ToAppState() =>
        new(ConversationId, Title, Status, "idle", [], IsPinned: IsPinned, AgentKey: InoModel.AgentKeyOf(ConversationId), ActiveRunId: ActiveRunId, Blocked: Blocked, BlockedReason: BlockedReason)
        {
            Pending = PendingPrompt is { } p ? new InoPending("p1", p, DateTimeOffset.UnixEpoch) : null,
            Error = ErrorCode is { } code ? new InoError(code, code == "paused" ? BlockedReason : null) : null,
            SelectedModel = SelectedModel,
            DetectedLocalModel = DetectedLocalModel,
            DetectedLocalModels = DetectedLocalModel is { } m ? [m] : null,
        };

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
            s.Pending?.Text,
            null,
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

    public static string ConversationKeyOf(string conversationId)
        => IConversation.KeyOf(ConversationId.Parse(ConversationIdOf(conversationId)));

    // Retained only for legacy import references and the tone observer.
    public static string AgentKeyOf(string conversationId) => $"ino/conversations/{conversationId}";
    public static string ToneKeyOf(string conversationId) => $"observer/tone/{AgentKeyOf(conversationId)}";
    public static string MintConversationId() => ConversationId.New().Value;

    public static string ConversationIdOf(string keyOrId)
    {
        if (string.IsNullOrWhiteSpace(keyOrId)) { return "main"; }
        var trimmed = keyOrId.Trim();
        const string legacyPrefix = "ino/conversations/";
        if (trimmed.StartsWith(legacyPrefix, StringComparison.Ordinal)) { return trimmed[legacyPrefix.Length..]; }
        const string currentPrefix = "conversations/";
        if (trimmed.StartsWith(currentPrefix, StringComparison.Ordinal)) { return trimmed[currentPrefix.Length..]; }
        return trimmed;
    }

    public static InoAppState SwitchConversation(InoAppState s, string conversationId, string[]? recentConversations = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var normalized = ConversationIdOf(conversationId);
        return s with
        {
            ConversationId = normalized,
            Title = normalized == s.ConversationId ? s.Title : $"Chat · {normalized[..Math.Min(8, normalized.Length)]}",
            Status = Ready,
            AgentKey = AgentKeyOf(normalized),
            ActiveRunId = null,
            Pending = null,
            Error = null,
            ActiveSection = "chat",
            RecentConversations = recentConversations ?? s.RecentConversations,
            ActiveSpecialistAppId = null,
            ActiveSpecialistHandle = null,
            ActiveSpecialistDescription = null,
            SpecialistStarters = [],
            SpecialistError = null,
        };
    }

    public static Rule IdleRule(InoSession session)
        => Decision.Rule("not_busy", session, static s => s.PendingPrompt is not null || s.ActiveRunId is not null, "Wait for the current reply to finish or stop it first.");

    public static Rule IdleRule(InoAppState state)
        => Decision.Rule("not_busy", state, static s => s.Pending is not null || s.ActiveRunId is not null, "Wait for the current reply to finish or stop it first.");

    public static Rule NotBlockedRule(InoSession session)
        => Decision.Rule("not_blocked", session, static s => s.Blocked, "Ino is paused until the pending interrupt is resolved.");

    public static Rule NotBlockedRule(InoAppState state)
        => Decision.Rule("not_blocked", state, static s => s.Blocked, "Ino is paused until the pending interrupt is resolved.");

    public static Rule NonEmptyPromptRule(string? text)
        => Decision.Rule("non_empty_prompt", text, static t => string.IsNullOrWhiteSpace(t), "Enter a message to send.");

    public static Rule WithinLimitRule(string? text)
        => Decision.Rule("within_max_chars", text, static t => (t?.Length ?? 0) > MaxMessageChars, $"Messages cannot exceed {MaxMessageChars:N0} characters.");

    public static Decision<InoAppState> DecideSend(InoAppState s, string? text, string pendingId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(s);
        var refusal = Decision.Check("send", NotBlockedRule(s), IdleRule(s), NonEmptyPromptRule(text), WithinLimitRule(text));
        if (refusal is not null)
        {
            return refusal.Rejection.Rules.Contains("within_max_chars", StringComparer.Ordinal)
                && !refusal.Rejection.Rules.Contains("not_busy", StringComparer.Ordinal)
                && !refusal.Rejection.Rules.Contains("not_blocked", StringComparer.Ordinal)
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

    public static Decision<string> DecideRetryPrompt(InoAppState s, ConversationView? view = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lastUser = view?.Entries.OfType<Said>().LastOrDefault(static e => e.By is Participant.Human)?.Text
            ?? view?.Runs.LastOrDefault(static r => !r.Posted && !string.IsNullOrWhiteSpace(r.UserText))?.UserText;
        var refusal = Decision.Check("retry", IdleRule(s), Decision.Rule("has_prior_prompt", lastUser, static u => string.IsNullOrWhiteSpace(u), "No prior message to try again."));
        return refusal is not null ? refusal : Decision.Accept(lastUser!);
    }

    /// <summary>The prompt text for message <paramref name="id"/> in <paramref name="view"/>, or null when none exists.</summary>
    public static string? PromptOf(ConversationView? view, string id)
    {
        if (view is null || string.IsNullOrWhiteSpace(id)) { return null; }
        var targetId = new EntryId(id);
        var runId = EntryIds.RunOf(targetId);
        if (!string.IsNullOrEmpty(runId))
        {
            var promptId = EntryIds.Said(runId);
            var said = view.Entries.OfType<Said>().FirstOrDefault(e => e.Id == promptId);
            if (!string.IsNullOrWhiteSpace(said?.Text)) { return said.Text; }
            var run = view.Runs.FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(run?.UserText)) { return run.UserText; }
        }

        var direct = view.Entries.OfType<Said>().FirstOrDefault(e => e.Id == targetId && e.By is Participant.Human);
        return string.IsNullOrWhiteSpace(direct?.Text) ? null : direct.Text;
    }

    internal static string DeriveTitle(string trimmed) => trimmed.Length <= 52 ? trimmed : trimmed[..52];

    internal static string NormalizeBlockReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "An interrupt holds the agent."
            : reason.StartsWith(BlockedPrefix, StringComparison.Ordinal) ? reason[BlockedPrefix.Length..]
            : reason;

    public static (InoAppState State, bool NeedsMigration) DecideOpen(
        InoAppState current, string? mode, string displayName, string? defaultModel, bool smart,
        Approval[] approvals, string[] grantSummaries)
    {
        ArgumentNullException.ThrowIfNull(current);
        var modelLabel = string.IsNullOrWhiteSpace(defaultModel) ? KnownModels.Defaults.Device[ModelRole.Chat].Value : defaultModel.Trim();
        var status = mode == "greet" ? $"Hello, {displayName}! Ready on {modelLabel}."
            : current.Blocked ? Paused : current.ActiveRunId is not null ? Working : Ready;
        var migrate = current.Turns.Length > 0 && string.IsNullOrEmpty(current.AgentKey);
        var next = Opened(current, smart, approvals, grantSummaries) with
        {
            Status = status,
            AgentKey = migrate ? current.AgentKey : AgentKeyOf(current.ConversationId),
            SelectedModel = modelLabel,
        };
        return (next, migrate);
    }

    public static InoAppState Opened(
        InoAppState s, bool smart, Approval[] approvals, string[] grantSummaries)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            Smart = smart,
            Approvals = approvals,
            GrantSummaries = grantSummaries,
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

    /// <summary>
    /// Folds the live <see cref="ConversationView"/> into Ino's screen state; messages stay in the conversation and are never copied into Ino's state.
    /// </summary>
    public static InoAppState FromConversation(InoAppState s, ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(view);
        if (!string.Equals(view.Id.Value, s.ConversationId, StringComparison.Ordinal))
        {
            return s;
        }

        var blocked = view.Blocked;
        var blockedReason = blocked ? NormalizeBlockReason(view.BlockedReason ?? s.BlockedReason) : null;
        var activeRunId = blocked ? null : view.Active?.RunId;
        var pending = blocked || s.Pending is null
            ? null
            : HasPersonSaid(view, s.Pending.Text) || activeRunId is not null
                ? null
                : s.Pending;

        var superseded = InoThread.SupersededIds(view);
        var (status, error) = ResolveConversationStatus(view, superseded, blocked, blockedReason, activeRunId is not null || pending is not null);

        var latestModel = LatestTurnModel(view, superseded);
        var selectedModel = !string.IsNullOrWhiteSpace(latestModel)
            && !string.Equals(latestModel, "default", StringComparison.OrdinalIgnoreCase)
            ? latestModel
            : s.SelectedModel;

        var projectedCards = view.Entries
            .OfType<Returned>()
            .Where(static r => r.Ok && r.Card?.Kind != "node" && !string.IsNullOrWhiteSpace(r.ResultJson))
            .Select(static r => InoCards.FromResult(r.ResultJson))
            .OfType<InoCard>()
            .ToArray();
        var manualCards = s.ActiveCards.Where(existing => projectedCards.All(p => !string.Equals(p.Id, existing.Id, StringComparison.Ordinal))).ToArray();

        return s with
        {
            Pending = pending,
            ActiveRunId = activeRunId,
            Blocked = blocked,
            BlockedReason = blockedReason,
            Status = status,
            Error = error,
            SelectedModel = selectedModel,
            Cards = manualCards.Length == 0 ? projectedCards : [.. manualCards, .. projectedCards],
        };
    }

    private static bool HasPersonSaid(ConversationView view, string text)
        => view.Entries.OfType<Said>().Any(e => e.By is Participant.Human
            && (string.Equals(e.Text, text, StringComparison.Ordinal)
                || e.Text.EndsWith("\n" + text, StringComparison.Ordinal)));

    /// <summary>
    /// Attaches the active inline drawer artifact/app context to a plain-English follow-up prompt so users
    /// never need legacy <c>"change &lt;alias&gt;:"</c> magic prefixes (while preserving backwards compatibility if typed).
    /// </summary>
    public static string AttachActiveContext(string prompt, InoAppState state)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(state);

        var trimmed = prompt.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return trimmed;
        }

        // Preserve legacy "change <alias>:" prefix if manually typed by a user.
        if (trimmed.StartsWith("change ", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':', StringComparison.Ordinal))
        {
            return trimmed;
        }

        var name = SanitizeContextToken(state.ActiveContextName);
        var target = SanitizeContextToken(state.ActiveContextTarget);
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(target))
        {
            return trimmed;
        }

        var contextHeader = !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(target) && !string.Equals(name, target, StringComparison.OrdinalIgnoreCase)
            ? $"[Active context: {name} ({target})]"
            : $"[Active context: {name ?? target}]";
        return $"{contextHeader}\n{trimmed}";
    }

    private static string? SanitizeContextToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var clean = raw
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Derives a valid sandbox unit alias slug for <c>ICodeOrchestration.CrystallizeAsync</c>.</summary>
    public static string SlugifyAlias(string? raw, string fallback = "pinned-app")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var sb = new System.Text.StringBuilder(raw.Length);
        var lastDash = false;
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0]))
        {
            slug = slug.Length == 0 ? fallback : $"app-{slug}";
        }

        return slug.Length <= 32 ? slug : slug[..32].TrimEnd('-');
    }

    private static string? LatestTurnModel(ConversationView view, HashSet<EntryId> superseded)
    {
        for (var i = view.Entries.Length - 1; i >= 0; i--)
        {
            if (view.Entries[i] is Said { By: Participant.Assistant, Model: { Value.Length: > 0 } model } said
                && !superseded.Contains(said.Id))
            {
                return model.Value;
            }
        }
        var lastRunModel = view.Runs.LastOrDefault(static r => !r.Posted && r.Model is { Provider: not AiProvider.Host, Model.Length: > 0 })?.Model?.Model;
        return lastRunModel;
    }

    private static (string Status, InoError? Error) ResolveConversationStatus(
        ConversationView view,
        HashSet<EntryId> superseded,
        bool blocked,
        string? blockedReason,
        bool busy)
    {
        if (blocked) { return (Paused, new InoError("paused", blockedReason ?? "An interrupt holds the agent.")); }
        if (busy) { return (Working, null); }

        var latestRun = view.Runs.LastOrDefault(static r => !r.Posted);
        if (latestRun is not null && !superseded.Contains(EntryIds.Reply(latestRun.RunId)))
        {
            if (latestRun.Status == AgentRunStatus.Failed)
            {
                return (Failed, new InoError(AgentFailures.CodeOf(latestRun.Failure)));
            }
            if (latestRun.Status == AgentRunStatus.Interrupted)
            {
                return (Failed, new InoError("interrupted"));
            }
        }

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
            ActiveSection = "chat",
        };
    }

    public static InoAppState ModelSelected(InoAppState s, string model, string status)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s with
        {
            Error = null,
            Status = status,
            SelectedModel = model,
        };
    }

    public static InoAppState SyncedSettings(InoAppState s, bool smart, string? defaultModel, string? displayName, string? changedModel)
    {
        ArgumentNullException.ThrowIfNull(s);
        var nextModel = string.IsNullOrWhiteSpace(defaultModel) ? s.SelectedModel : defaultModel.Trim();
        var sameModel = string.Equals(s.SelectedModel, nextModel, StringComparison.Ordinal);
        var keepStatus = s.Status.StartsWith("Switched model to ", StringComparison.Ordinal)
            || (sameModel && s.Status.StartsWith("Default Chat model set to ", StringComparison.Ordinal))
            || s.Status.StartsWith("Switched conversation to ", StringComparison.Ordinal);
        return keepStatus
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

    public static InoAppState DecideSlashHelp(InoAppState s, string cmd)
        => s with { Status = $"Unknown command /{cmd}. Commands: /new, /rename <title>, /pin, /model <id>, /compact, /clear." };

    public static InoAppState DecideMake(InoAppState s, string target, string sentence, string brainId)
    {
        ArgumentNullException.ThrowIfNull(s);
        var cardId = $"app-{target}";
        var appKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), target).Key;
        var next = s with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. s.ActiveCards, new InoCard(cardId, "applet", $"Live App: {target}", sentence, $"neuron://{appKey}", "running", $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), "ino").Key}#{cardId}")],
        };
        return next.OpenArtifactDrawer(new InoDrawerArtifact(
            Id: cardId,
            Title: target,
            Kind: "app",
            Status: "done",
            LiveViewPath: $"apps/{target}/view",
            Summary: sentence,
            PinnedAppAlias: target));
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
            var (visual, detail) = InoView.ResolveActivityPresentation(new InoActivity(string.Empty, combined));
            return string.IsNullOrWhiteSpace(detail) ? visual.Label : $"{visual.Label} · {detail}";
        }
        catch (JsonException) { return trimmed; }
    }
}
