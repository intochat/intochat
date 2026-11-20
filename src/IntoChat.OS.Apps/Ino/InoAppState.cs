using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Ino;

public sealed record InoTurn(
    string UserMessage,
    string Reply,
    string RouteHint,
    int RecalledCount);

public sealed record InoActivity(
    string Id,
    string Title,
    string? Result = null,
    string? Error = null);

public sealed record InoMessage(
    string Id,
    string Role,
    string Text,
    string Status = "completed")
{
    public string? Model { get; init; }
    public string? Failure { get; init; }
    public InoActivity[] Activities { get; init; } = [];
    public InoCard[] Cards { get; init; } = [];
}

public sealed record InoPending(
    string Id,
    string Text,
    DateTimeOffset At);

public sealed record InoError(
    string Code,
    string? Detail = null);

/// <summary>
/// Pure DDD Aggregate state for <see cref="InoApp"/> (<c>app.ino</c>).
/// Enforces active thread and section validity, bounded 64-thread history, non-null collections,
/// and declarative <see cref="Rule"/> guards for thread switching, prompt submission, model/provider setup, and approvals.
/// </summary>
public sealed record InoAppState(
    string ConversationId,
    string Title,
    string Status,
    string VoiceStatus,
    InoTurn[] Turns,
    InoCard[]? Cards = null,
    string? ConnectedDatabase = null,
    bool IsPinned = true,
    string? AgentKey = null,
    string? ActiveRunId = null,
    bool Blocked = false,
    string[]? GrantSummaries = null,
    string? BlockedReason = null,
    string[]? SeenProgress = null)
{
    public const int MaxRecentConversations = 64;
    public const int MaxPendingApprovals = 16;
    public const int MinProviderCredentialLength = 4;

    public static readonly string[] SupportedSections = ["chat", "approvals", "setup", "data"];
    public static readonly string[] SupportedProviders = ["openai", "anthropic", "google", "openrouter", "ollama"];

    private readonly InoTurn[] _turns = Turns ?? [];
    private readonly InoCard[] _cards = Cards ?? [];
    private readonly string[] _grantSummaries = GrantSummaries ?? [];
    private readonly string[] _seenProgress = SeenProgress ?? [];
    private readonly Approval[] _approvals = [];
    private readonly string[] _detectedLocalModels = [];
    private readonly string[] _recentConversations = [];

    public InoAppState() : this("main", "New chat", "Ready", "idle", [], [], null, true) { }

    [AllowNull]
    public InoTurn[] Turns
    {
        get => _turns;
        init => _turns = value ?? [];
    }

    [AllowNull]
    public InoCard[] Cards
    {
        get => _cards;
        init => _cards = value ?? [];
    }

    [AllowNull]
    public string[] GrantSummaries
    {
        get => _grantSummaries;
        init => _grantSummaries = value ?? [];
    }

    [AllowNull]
    public string[] SeenProgress
    {
        get => _seenProgress;
        init => _seenProgress = value ?? [];
    }

    [JsonIgnore]
    public InoCard[] ActiveCards => _cards;

    public InoMessage[]? Messages { get; init; }
    public long ChatRevision { get; init; }
    public InoPending? Pending { get; init; }
    public InoError? Error { get; init; }

    [AllowNull]
    public Approval[] Approvals
    {
        get => _approvals;
        init => _approvals = value ?? [];
    }

    public bool Smart { get; init; }
    public string? SelectedModel { get; init; }
    public string? DetectedLocalModel { get; init; }

    [AllowNull]
    public string[] DetectedLocalModels
    {
        get => _detectedLocalModels;
        init => _detectedLocalModels = value ?? [];
    }

    [AllowNull]
    public string[] RecentConversations
    {
        get => _recentConversations;
        init => _recentConversations = NormalizeRecentConversations(value);
    }

    public string? ContextSummary { get; init; }
    public bool DevUiMode { get; init; }
    public string ActiveSection { get; init; } = "chat";
    public string? ActiveFocusTarget { get; init; }
    public string? DraftPrompt { get; init; }
    public bool HasUnsavedProviderDraft { get; init; }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("Active ConversationId and Title are never blank."), JsonIgnore]
    public bool ValidActiveThreadInvariant =>
        !string.IsNullOrWhiteSpace(ConversationId)
        && !string.IsNullOrWhiteSpace(Title);

    [Invariant("ActiveSection is always chat, approvals, setup, or data."), JsonIgnore]
    public bool ValidActiveSectionInvariant =>
        SupportedSections.Contains(ActiveSection, StringComparer.Ordinal);

    [Invariant("Recent conversation history is bounded to at most 64 non-empty thread entries."), JsonIgnore]
    public bool BoundedThreadHistoryInvariant =>
        RecentConversations is not null
        && RecentConversations.Length <= MaxRecentConversations
        && RecentConversations.All(static id => !string.IsNullOrWhiteSpace(id));

    [Invariant("State collections are never null."), JsonIgnore]
    public bool NonNullCollectionsInvariant =>
        Turns is not null
        && Cards is not null
        && GrantSummaries is not null
        && SeenProgress is not null
        && Approvals is not null
        && DetectedLocalModels is not null
        && RecentConversations is not null;

    [Invariant("ChatRevision is non-negative and blocked sessions hold neither an active run nor a pending prompt."), JsonIgnore]
    public bool ConsistentSessionRunInvariant =>
        ChatRevision >= 0
        && (!Blocked || (ActiveRunId is null && Pending is null));

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedSectionRule(string? section) => Decision.Rule(
        "invalid_section",
        section,
        static s => string.IsNullOrWhiteSpace(s) || !SupportedSections.Contains(s.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Active Ino section must be chat, approvals, setup, or data.");

    public static Rule ValidThreadSelectionRule(string? conversationId) => Decision.Rule(
        "invalid_thread_id",
        conversationId,
        static id => string.IsNullOrWhiteSpace(id),
        "A non-empty conversation identifier is required to switch threads.");

    public Rule DistinctThreadSelectionRule(string? conversationId) => Decision.Rule(
        "already_on_thread",
        (Current: ConversationId, Candidate: string.IsNullOrWhiteSpace(conversationId) ? string.Empty : InoModel.ConversationIdOf(conversationId)),
        static x => !string.IsNullOrEmpty(x.Candidate) && string.Equals(x.Current, x.Candidate, StringComparison.Ordinal),
        static x => $"Conversation '{x.Candidate}' is already active.");

    [JsonIgnore]
    public Rule NotBlockedRule => InoModel.NotBlockedRule(this);

    [JsonIgnore]
    public Rule IdleRule => InoModel.IdleRule(this);

    public static Rule ValidModelRule(string? model) => Decision.Rule(
        "invalid_model",
        model,
        static m => string.IsNullOrWhiteSpace(m) || m.Trim().Any(char.IsWhiteSpace) || m.Trim().Any(char.IsControl),
        "Model identifier must be non-empty and contain no whitespace.");

    public static Rule SupportedProviderRule(string? provider) => Decision.Rule(
        "unsupported_provider",
        provider,
        static p => string.IsNullOrWhiteSpace(p) || !SupportedProviders.Contains(p.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Provider must be openai, anthropic, google, openrouter, or ollama.");

    public static Rule OptionalProviderRule(string? provider) => Decision.Rule(
        "unsupported_provider",
        provider,
        static p => !string.IsNullOrWhiteSpace(p) && !SupportedProviders.Contains(p.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Provider must be openai, anthropic, google, openrouter, or ollama.");

    public static Rule ValidCredentialRule(string? credential) => Decision.Rule(
        "invalid_provider_credential",
        credential,
        static c => string.IsNullOrWhiteSpace(c) || c.Trim().Length < MinProviderCredentialLength || c.Trim().Any(char.IsWhiteSpace),
        "Provider API key must be non-empty and free of whitespace.");

    public static Rule ValidApprovalPlanRule(string? planId) => Decision.Rule(
        "invalid_approval_plan",
        planId,
        static id => string.IsNullOrWhiteSpace(id),
        "Approval plan identifier is required.");

    public Rule DuplicateApprovalPlanRule(string? planId) => Decision.Rule(
        "duplicate_approval_plan",
        (Approvals: Approvals ?? [], PlanId: planId?.Trim() ?? string.Empty),
        static x => !string.IsNullOrEmpty(x.PlanId) && x.Approvals.Any(a => string.Equals(a.PlanId, x.PlanId, StringComparison.Ordinal)),
        static x => $"Approval plan '{x.PlanId}' is already pending.");

    public Rule PendingApprovalExistsRule(string? planId) => Decision.Rule(
        "approval_not_found",
        (Approvals: Approvals ?? [], PlanId: planId?.Trim() ?? string.Empty),
        static x => string.IsNullOrEmpty(x.PlanId) || !x.Approvals.Any(a => string.Equals(a.PlanId, x.PlanId, StringComparison.Ordinal)),
        "Pending approval plan was not found.");

    public Rule CanDepart(bool force = false) => Decision.Require(
        "ino_setup_unsaved",
        (Force: force, HasUnsavedDraft: HasUnsavedProviderDraft),
        static x => x.Force || !x.HasUnsavedDraft,
        "Finish or discard provider credential setup before leaving.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public Decision<InoAppState> SelectSection(
        [Sample("chat", "approvals", "invalid-section")] string section)
    {
        if (Decision.Check("select-section", SupportedSectionRule(section)) is { } refused)
        {
            return refused;
        }

        var normalized = section.Trim().ToLowerInvariant();
        return Decision.Accept(this with { ActiveSection = normalized });
    }

    public Decision<InoAppState> SelectThread(
        [Sample("c-0123456789abcdef0123456789abcdef", "main", "")] string conversationId)
    {
        if (Decision.Check(
                "switch-conversation",
                ValidThreadSelectionRule(conversationId),
                DistinctThreadSelectionRule(conversationId)) is { } refused)
        {
            return refused;
        }

        var normalized = InoModel.ConversationIdOf(conversationId);
        var updatedRecents = NormalizeRecentConversations(
            [InoModel.AgentKeyOf(ConversationId), .. RecentConversations]);
        return Decision.Accept(InoModel.SwitchConversation(this, normalized, updatedRecents) with
        {
            DraftPrompt = null,
        });
    }

    public Decision<InoAppState> SubmitPrompt(
        [Sample("Summarize Q4 renewals", "")] string prompt)
    {
        var decision = InoModel.DecideSend(this, prompt, "p-1", DateTimeOffset.UnixEpoch);
        return decision.IsAccepted
            ? Decision.Accept(decision.Next with { DraftPrompt = null })
            : decision;
    }

    public Decision<InoAppState> ConfigureModel(
        [Sample("qwen2.5-coder:14b", "")] string model)
    {
        if (Decision.Check("use-local", ValidModelRule(model)) is { } refused)
        {
            return refused;
        }

        var trimmed = model.Trim();
        var nextError = Error?.Code is "no-provider" or "provider-rejected-key" ? null : Error;
        var nextStatus = ActiveRunId is not null || Pending is not null
            ? InoModel.Working
            : Blocked
                ? InoModel.Paused
                : $"Switched model to {trimmed}";
        return Decision.Accept(this with
        {
            SelectedModel = trimmed,
            Error = nextError,
            HasUnsavedProviderDraft = false,
            Status = nextStatus,
        });
    }

    public Decision<InoAppState> StageProviderSetup(
        [Sample("openai", "unknown-provider")] string provider,
        [Sample("sk-proj-validkey1234", "bad")] string credential)
    {
        if (Decision.Check(
                "save-provider",
                SupportedProviderRule(provider),
                ValidCredentialRule(credential)) is { } refused)
        {
            return refused;
        }

        var cleanProvider = provider.Trim().ToLowerInvariant();
        var defaultModel = InoModelSetup.DefaultModelForProvider(cleanProvider);
        return Decision.Accept(this with
        {
            SelectedModel = defaultModel,
            Error = null,
            HasUnsavedProviderDraft = false,
            ActiveSection = "chat",
            Status = $"Saved {cleanProvider} credential · Ready on {defaultModel}",
        });
    }

    public Decision<InoAppState> RecordPendingApproval(
        [Sample("plan-1", "")] string planId,
        [Sample("Open pull request?")] string title = "Open pull request?")
    {
        if (Decision.Check(
                "record-approval",
                ValidApprovalPlanRule(planId),
                DuplicateApprovalPlanRule(planId)) is { } refused)
        {
            return refused;
        }

        var cleanId = planId.Trim();
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Pending approval" : title.Trim();
        var approval = new Approval(
            cleanId,
            ApprovalKind.FlowStep,
            $"flow-{cleanId}",
            cleanTitle,
            "Awaiting person confirmation.",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            $"trace-{cleanId}");
        Approval[] nextApprovals = [.. Approvals.TakeLast(MaxPendingApprovals - 1), approval];
        return Decision.Accept(this with
        {
            Approvals = nextApprovals,
            ActiveSection = "approvals",
        });
    }

    public Decision<InoAppState> DismissResolvedApproval(
        [Sample("plan-1", "plan-missing")] string planId)
    {
        if (Decision.Check("resolve-approval", PendingApprovalExistsRule(planId)) is { } refused)
        {
            return refused;
        }

        var cleanId = planId.Trim();
        var remaining = Approvals.Where(a => !string.Equals(a.PlanId, cleanId, StringComparison.Ordinal)).ToArray();
        return Decision.Accept(this with
        {
            Approvals = remaining,
            ActiveSection = remaining.Length == 0 && ActiveSection == "approvals" ? "chat" : ActiveSection,
        });
    }

    public InoAppState ToggleDevUi() => this with { DevUiMode = !DevUiMode };

    public InoAppState SetProviderSetupDraft([Sample(true, false)] bool hasDraft) => this with
    {
        HasUnsavedProviderDraft = hasDraft,
        ActiveSection = hasDraft ? "setup" : (ActiveSection == "setup" ? "chat" : ActiveSection),
    };

    public Decision<InoAppState> StageNavigationPrompt(
        [Sample("Triage GitHub issue #42", "")] string prompt,
        [Sample("tasks/approvals")] string? focusTarget = null)
    {
        if (Decision.Check(
                "stage-navigation-prompt",
                InoModel.NonEmptyPromptRule(prompt),
                InoModel.WithinLimitRule(prompt)) is { } refused)
        {
            return refused;
        }

        return Decision.Accept(this with
        {
            DraftPrompt = prompt.Trim(),
            ActiveFocusTarget = string.IsNullOrWhiteSpace(focusTarget) ? ActiveFocusTarget : focusTarget.Trim(),
            ActiveSection = "chat",
        });
    }

    public InoAppState DismissNavigationSeed() => this with
    {
        DraftPrompt = null,
        ActiveFocusTarget = null,
    };

    [IgnoreExplore]
    public InoAppState ApplyNavigation(InoNavArgs? args)
    {
        if (args is null)
        {
            return this;
        }

        var next = this;
        if (!string.IsNullOrWhiteSpace(args.FocusTarget))
        {
            next = next with { ActiveFocusTarget = args.FocusTarget.Trim() };
        }

        if (!string.IsNullOrWhiteSpace(args.Prompt))
        {
            var cleanPrompt = args.Prompt.Trim();
            if (cleanPrompt.Length > InoModel.MaxMessageChars)
            {
                cleanPrompt = cleanPrompt[..InoModel.MaxMessageChars];
            }

            next = next with
            {
                DraftPrompt = cleanPrompt,
                ActiveSection = "chat",
            };
        }

        return next;
    }

    public static string[] NormalizeRecentConversations(IEnumerable<string>? source) =>
        source is null
            ? []
            : [.. source
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(MaxRecentConversations)];

    public override string ToString()
        => $"{Status} · {ConversationId} ({ActiveSection})";
}
