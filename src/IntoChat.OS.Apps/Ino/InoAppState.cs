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

public sealed record InoEngineerCandidate(
    int InstanceIndex,
    int Rank,
    string StrategyLabel,
    string Status,
    int TotalScore,
    int ChangedLines,
    bool IsSelected,
    bool BuildPassed,
    bool TestsPassed,
    bool LiveVerifyPassed,
    bool ZeroTelemetryErrors,
    bool SelfReviewPassed,
    string? SelfReviewSummary = null,
    string? CommitSha = null,
    string? BranchName = null,
    string? FailureDetail = null);

public sealed record InoDrawerArtifact(
    string Id,
    string Title,
    string Kind,
    string Status,
    string? FlowId = null,
    string? LiveViewPath = null,
    string? CompiledScript = null,
    string? Summary = null,
    string? DiffText = null,
    string? LogsText = null,
    string[]? ScopedContracts = null,
    string? PinnedAppAlias = null,
    string? PinError = null,
    string? ExecutionTarget = null,
    string? CandidateSha = null,
    string? RepoSlug = null,
    string? HeadBranch = null,
    string? BaseBranch = null,
    string? PullRequestUrl = null,
    bool SelfReviewPassed = false,
    string? SelfReviewSummary = null,
    string? ApprovalError = null,
    string? VerificationSummary = null,
    InoEngineerCandidate[]? TournamentCandidates = null,
    bool BuildVerified = false,
    bool LiveVerified = false,
    bool HasVerificationReceipt = false,
    string? ConfirmationPlanId = null,
    string? PushedRef = null)
{
    public const string EngineerPrKind = "engineer-pr";

    private readonly string[] _scopedContracts = ScopedContracts ?? [];
    private readonly InoEngineerCandidate[] _tournamentCandidates = TournamentCandidates ?? [];

    [AllowNull]
    public string[] ScopedContracts
    {
        get => _scopedContracts;
        init => _scopedContracts = value ?? [];
    }

    [AllowNull]
    public InoEngineerCandidate[] TournamentCandidates
    {
        get => _tournamentCandidates;
        init => _tournamentCandidates = value ?? [];
    }

    public bool Equals(InoDrawerArtifact? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Status, other.Status, StringComparison.Ordinal)
        && string.Equals(FlowId, other.FlowId, StringComparison.Ordinal)
        && string.Equals(LiveViewPath, other.LiveViewPath, StringComparison.Ordinal)
        && string.Equals(CompiledScript, other.CompiledScript, StringComparison.Ordinal)
        && string.Equals(Summary, other.Summary, StringComparison.Ordinal)
        && string.Equals(DiffText, other.DiffText, StringComparison.Ordinal)
        && string.Equals(LogsText, other.LogsText, StringComparison.Ordinal)
        && string.Equals(PinnedAppAlias, other.PinnedAppAlias, StringComparison.Ordinal)
        && string.Equals(PinError, other.PinError, StringComparison.Ordinal)
        && string.Equals(ExecutionTarget, other.ExecutionTarget, StringComparison.Ordinal)
        && string.Equals(CandidateSha, other.CandidateSha, StringComparison.Ordinal)
        && string.Equals(RepoSlug, other.RepoSlug, StringComparison.Ordinal)
        && string.Equals(HeadBranch, other.HeadBranch, StringComparison.Ordinal)
        && string.Equals(BaseBranch, other.BaseBranch, StringComparison.Ordinal)
        && string.Equals(PullRequestUrl, other.PullRequestUrl, StringComparison.Ordinal)
        && SelfReviewPassed == other.SelfReviewPassed
        && string.Equals(SelfReviewSummary, other.SelfReviewSummary, StringComparison.Ordinal)
        && string.Equals(ApprovalError, other.ApprovalError, StringComparison.Ordinal)
        && string.Equals(VerificationSummary, other.VerificationSummary, StringComparison.Ordinal)
        && BuildVerified == other.BuildVerified
        && LiveVerified == other.LiveVerified
        && HasVerificationReceipt == other.HasVerificationReceipt
        && string.Equals(ConfirmationPlanId, other.ConfirmationPlanId, StringComparison.Ordinal)
        && string.Equals(PushedRef, other.PushedRef, StringComparison.Ordinal)
        && _scopedContracts.SequenceEqual(other._scopedContracts, StringComparer.Ordinal)
        && _tournamentCandidates.SequenceEqual(other._tournamentCandidates);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Title, StringComparer.Ordinal);
        hash.Add(Kind, StringComparer.Ordinal);
        hash.Add(Status, StringComparer.Ordinal);
        hash.Add(FlowId, StringComparer.Ordinal);
        hash.Add(LiveViewPath, StringComparer.Ordinal);
        hash.Add(PinnedAppAlias, StringComparer.Ordinal);
        hash.Add(PinError, StringComparer.Ordinal);
        hash.Add(CandidateSha, StringComparer.Ordinal);
        hash.Add(RepoSlug, StringComparer.Ordinal);
        hash.Add(PullRequestUrl, StringComparer.Ordinal);
        hash.Add(SelfReviewPassed);
        hash.Add(SelfReviewSummary, StringComparer.Ordinal);
        hash.Add(ApprovalError, StringComparer.Ordinal);
        hash.Add(BuildVerified);
        hash.Add(LiveVerified);
        hash.Add(HasVerificationReceipt);
        hash.Add(ConfirmationPlanId, StringComparer.Ordinal);
        hash.Add(PushedRef, StringComparer.Ordinal);
        foreach (var c in _scopedContracts) { hash.Add(c, StringComparer.Ordinal); }
        foreach (var candidate in _tournamentCandidates) { hash.Add(candidate); }
        return hash.ToHashCode();
    }
}

public sealed record InoTaskSummary(
    string TaskId,
    string Title,
    string Status,
    string? Summary = null,
    string? FlowId = null,
    string? ArtifactUri = null,
    string? ArtifactLabel = null,
    string? AskQuestion = null,
    string[]? AskOptions = null,
    int? AskRecommendedIndex = null,
    string? ConfirmationPlanId = null)
{
    private readonly string[] _askOptions = AskOptions ?? [];

    [AllowNull]
    public string[] AskOptions
    {
        get => _askOptions;
        init => _askOptions = value ?? [];
    }

    public bool Equals(InoTaskSummary? other) =>
        other is not null
        && string.Equals(TaskId, other.TaskId, StringComparison.Ordinal)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Status, other.Status, StringComparison.Ordinal)
        && string.Equals(Summary, other.Summary, StringComparison.Ordinal)
        && string.Equals(FlowId, other.FlowId, StringComparison.Ordinal)
        && string.Equals(ArtifactUri, other.ArtifactUri, StringComparison.Ordinal)
        && string.Equals(ArtifactLabel, other.ArtifactLabel, StringComparison.Ordinal)
        && string.Equals(AskQuestion, other.AskQuestion, StringComparison.Ordinal)
        && AskRecommendedIndex == other.AskRecommendedIndex
        && string.Equals(ConfirmationPlanId, other.ConfirmationPlanId, StringComparison.Ordinal)
        && _askOptions.SequenceEqual(other._askOptions, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TaskId, StringComparer.Ordinal);
        hash.Add(Title, StringComparer.Ordinal);
        hash.Add(Status, StringComparer.Ordinal);
        hash.Add(FlowId, StringComparer.Ordinal);
        hash.Add(AskQuestion, StringComparer.Ordinal);
        hash.Add(AskRecommendedIndex);
        foreach (var opt in _askOptions) { hash.Add(opt, StringComparer.Ordinal); }
        return hash.ToHashCode();
    }
}

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

    public static readonly string[] SupportedSections = ["chat", "chats", "approvals", "setup", "data"];
    public static readonly string[] SupportedDrawerTabs = ["preview", "diff", "logs", "plan", "pinned-apps"];
    public static readonly string[] SupportedProviders = ["openai", "anthropic", "google", "openrouter", "ollama"];

    private readonly InoTurn[] _turns = Turns ?? [];
    private readonly InoCard[] _cards = Cards ?? [];
    private readonly string[] _grantSummaries = GrantSummaries ?? [];
    private readonly string[] _seenProgress = SeenProgress ?? [];
    private readonly Approval[] _approvals = [];
    private readonly string[] _detectedLocalModels = [];
    private readonly string[] _recentConversations = [];
    private readonly InoTaskSummary[] _activeTasks = [];
    private readonly string[] _specialistStarters = [];
    private readonly InoEngineerRunSummary[] _squadEngineerRuns = [];

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

    // Legacy; only cleared to null by InoApp.Commit, see OWED.md.
    public InoMessage[]? Messages { get; init; }
    // Legacy, unwritten; read by nothing, see OWED.md.
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
    public InoDrawerArtifact? ActiveArtifact { get; init; }
    public bool DrawerOpen { get; init; }
    public string DrawerTab { get; init; } = "preview";
    public string? ActiveContextName { get; init; }
    public string? ActiveContextTarget { get; init; }
    public string? LastAutoOpenedFlowId { get; init; }

    [AllowNull]
    public InoTaskSummary[] ActiveTasks
    {
        get => _activeTasks;
        init => _activeTasks = value ?? [];
    }

    public string? ActiveSpecialistAppId { get; init; }
    public string? ActiveSpecialistHandle { get; init; }
    public string? ActiveSpecialistDescription { get; init; }

    [AllowNull]
    public string[] SpecialistStarters
    {
        get => _specialistStarters;
        init => _specialistStarters = value ?? [];
    }

    public string? SpecialistPanelMode { get; init; }
    public string? SpecialistError { get; init; }

    [AllowNull]
    public InoEngineerRunSummary[] SquadEngineerRuns
    {
        get => _squadEngineerRuns;
        init => _squadEngineerRuns = value ?? [];
    }

    public string? SelectedSquadTaskId { get; init; }
    public string? ChattingAboutChoiceTaskId { get; init; }
    public string? SquadError { get; init; }
    public bool SidebarCollapsed { get; init; }
    public bool NewMenuOpen { get; init; }
    public bool HistoryOpen { get; init; }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("DrawerTab is always preview, diff, logs, or plan."), JsonIgnore]
    public bool ValidDrawerTabInvariant =>
        SupportedDrawerTabs.Contains(DrawerTab, StringComparer.Ordinal);

    [Invariant("Active ConversationId and Title are never blank."), JsonIgnore]
    public bool ValidActiveThreadInvariant =>
        !string.IsNullOrWhiteSpace(ConversationId)
        && !string.IsNullOrWhiteSpace(Title);

    [Invariant("ActiveSection is always chat, chats, approvals, setup, or data."), JsonIgnore]
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
        && RecentConversations is not null
        && ActiveTasks is not null
        && SquadEngineerRuns is not null;

    [Invariant("Blocked sessions hold neither an active run nor a pending prompt."), JsonIgnore]
    public bool ConsistentSessionRunInvariant =>
        !Blocked || (ActiveRunId is null && Pending is null);

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedSectionRule(string? section) => Decision.Rule(
        "invalid_section",
        section,
        static s => string.IsNullOrWhiteSpace(s) || !SupportedSections.Contains(s.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Active Ino section must be chat, chats, approvals, setup, or data.");

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
        var defaultModel = InoModelSetup.DefaultModelForProvider(cleanProvider).Value;
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

    public static Rule SupportedDrawerTabRule(string? tab) => Decision.Rule(
        "invalid_drawer_tab",
        tab,
        static t =>
        {
            if (string.IsNullOrWhiteSpace(t))
            {
                return true;
            }

            var normalized = NormalizeDrawerTab(t);
            return !SupportedDrawerTabs.Contains(normalized, StringComparer.Ordinal);
        },
        "Drawer tab must be preview, diff, logs, or plan.");

    public Decision<InoAppState> SelectDrawerTab(
        [Sample("preview", "diff", "logs", "plan", "invalid-tab")] string tab)
    {
        if (Decision.Check("drawer-tab", SupportedDrawerTabRule(tab)) is { } refused)
        {
            return refused;
        }

        return Decision.Accept(this with { DrawerTab = NormalizeDrawerTab(tab) });
    }

    private static string NormalizeDrawerTab(string raw)
    {
        var trimmed = raw.Trim().ToLowerInvariant();
        return string.Equals(trimmed, "tournament", StringComparison.Ordinal) ? "preview" : trimmed;
    }

    [IgnoreExplore]
    public InoAppState OpenArtifactDrawer(InoDrawerArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var contextTarget = !string.IsNullOrWhiteSpace(artifact.PinnedAppAlias)
            ? artifact.PinnedAppAlias
            : !string.IsNullOrWhiteSpace(artifact.FlowId)
                ? artifact.FlowId
                : artifact.Id;
        return this with
        {
            ActiveArtifact = artifact,
            DrawerOpen = true,
            DrawerTab = SupportedDrawerTabs.Contains(DrawerTab, StringComparer.Ordinal) ? DrawerTab : "preview",
            ActiveContextName = string.IsNullOrWhiteSpace(artifact.Title) ? contextTarget : artifact.Title.Trim(),
            ActiveContextTarget = contextTarget,
        };
    }

    public InoAppState CloseArtifactDrawer() => this with
    {
        DrawerOpen = false,
    };

    public InoAppState ClearActiveContext() => this with
    {
        ActiveContextName = null,
        ActiveContextTarget = null,
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
