/*
# IntoChat Engineer
Role: Studio
Shipped: true
Operations: open, new-run, brainstorm, approve-plan, run-verify, approve-pr, clean-worktree, select-tab, select-instance, select-candidate, reply-review, ui.command

Autonomous multi-worktree engineering workbench (`app.engineer`) orchestrating `flow.engineer` across 1..N isolated
local git worktrees or cloud sandbox volumes with User + Architect + Critic brainstorming, Roslyn C# + Dart code edits,
Release build/test + live Playwright/MCP verification, parallel candidate tournament scoring, and SHA-bound human PR approval.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.Chat.Signals;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.CommandBar.Signals;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Stepper.Signals;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Tabs.Signals;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.Modules.UI.TreeView.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Apps.Engineer;

[Signal("engineer.open")]
public sealed record EngineerOpen(string? Tab = null, string? FlowId = null) : Signal;

[Signal("engineer.new-run")]
public sealed record EngineerNewRun(string? Repository = null, string? Goal = null, int? InstanceCount = null, string? Backend = null) : Signal;

[Signal("engineer.brainstorm")]
public sealed record EngineerBrainstorm(string? Prompt = null) : Signal;

[Signal("engineer.approve-plan")]
public sealed record EngineerApprovePlan : Signal;

[Signal("engineer.run-verify")]
public sealed record EngineerRunVerify : Signal;

[Signal("engineer.approve-pr")]
public sealed record EngineerApprovePr : Signal;

[Signal("engineer.clean-worktree")]
public sealed record EngineerCleanWorktree : Signal;

[Signal("engineer.select-tab")]
public sealed record EngineerSelectTab(string? Tab = null) : Signal;

[Signal("engineer.select-instance")]
public sealed record EngineerSelectInstance(int InstanceIndex = 0, string? FilePath = null) : Signal;

[Signal("engineer.select-candidate")]
public sealed record EngineerSelectCandidate(int CandidateIndex = 0, string? FilePath = null) : Signal;

[Signal("engineer.reply-review")]
public sealed record EngineerReplyReview(int PullRequestNumber = 0, long CommentId = 0, string? Body = null) : Signal;

[Neuron("app.engineer")]
[Screen("app.engineer", Title = "IntoChat Engineer", Icon = "workflow")]
public sealed partial class EngineerApp : AggregateScreen<EngineerWorkspaceState>, INavigatedTo<StudioNavArgs>, INavigatedFrom
{
    public const string AppName = "engineer";

    public const string CommandBarKey = "engineer/command-bar";
    public const string StepperKey = "engineer/stepper";
    public const string WorktreeTreeKey = "engineer/tree/worktrees";
    public const string WorkspaceTabsKey = "engineer/tabs";
    public const string BrainstormChatKey = "engineer/chat/brainstorm";
    public const string CodeEditorKey = "engineer/editor/code";
    public const string TasksTableKey = "engineer/table/tasks";
    public const string KanbanKey = "engineer/kanban/tasks";

    private const string FlowsKey = "flows/index";

    [Slot(Key = FlowsKey, Required = false)] private partial IFlows? Flows { get; }
    [Slot(Key = "connection/github/main", Required = false)] private partial IRepository? MainRepository { get; }
    [Slot(Required = false)] private partial SlotCollection<IRepository> Repositories { get; }
    [Slot(Key = CommandBarKey, Required = false)] private partial ICommandBar? CommandBar { get; }
    [Slot(Key = StepperKey, Required = false)] private partial IStepper? Stepper { get; }
    [Slot(Key = WorkspaceTabsKey, Required = false)] private partial ITabs? WorkspaceTabs { get; }
    [Slot(Key = WorktreeTreeKey, Required = false)] private partial ITreeView? WorktreeTree { get; }
    [Slot(Key = CodeEditorKey, Required = false)] private partial ICodeEditor? CodeEditor { get; }
    [Slot(Key = KanbanKey, Required = false)] private partial IKanbanBoard? Kanban { get; }
    [Slot(Key = TasksTableKey, Required = false)] private partial ITable? TasksTable { get; }
    [Slot(Key = BrainstormChatKey, Required = false)] private partial IChatThread? BrainstormChat { get; }

    private static readonly ILogger ConsoleLogger = OsConsoleLog.For<EngineerApp>();
    private readonly ILogger _logger;
    private readonly IServiceProvider? _services;
    private string? _lastSyncedChatSignature;
    private int _syncedStepperVersion;
    private int _syncedTabsVersion;
    private int _syncedTreeVersion;

    public EngineerApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public EngineerApp(ILogger<EngineerApp>? logger = null, IServiceProvider? services = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
        _services = services;
    }

    public static AppBundle Bundle { get; } = new(
        AppName,
        "1.0.0",
        new AppManifest(
            Title: "IntoChat Engineer",
            Description: "Autonomous multi-worktree engineering workbench: brainstorm, plan, Roslyn/Dart code, live verify, candidate tournament, and PR review.",
            Operations:
            [
                new("open", "Open the IntoChat Engineer workbench."),
                new("new-run", "Start a new 1..N parallel worktree engineering run."),
                new("brainstorm", "Run User + Architect + Critic multi-perspective planning."),
                new("approve-plan", "Approve the rule-checked EngineerTaskPlan and prepare isolated worktrees."),
                new("run-verify", "Execute Release build, flutter analyze, unit tests, and live Playwright/MCP verification."),
                new("approve-pr", "Approve and publish the verified commit SHA as a GitHub Draft Pull Request."),
                new("clean-worktree", "Clean up all isolated worktree leases idempotently."),
                new("select-tab", "Switch between Plan, Code, Verification & Traces, and PR Review tabs."),
                new("select-instance", "Focus a parallel worktree instance or changed file."),
                new("select-candidate", "Promote a verified candidate worktree diff for PR approval."),
                new("reply-review", "Reply to a GitHub pull request review comment thread."),
                new(UiCommand.SignalAlias, "Dispatch a typed Engineer aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "workflow",
            Toolbar:
            [
                new("new-run", Icon: "plus", Label: "New Run"),
                new("brainstorm", Icon: "sparkles", Label: "Brainstorm"),
                new("run-verify", Icon: "play", Label: "Run Verify"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(EngineerApp).AssemblyQualifiedName,
    };

    public static string CandidateSelectKey(int instanceIndex)
        => string.Create(CultureInfo.InvariantCulture, $"engineer:candidate:select:{instanceIndex + 1}");

    public new EngineerWorkspaceState State => base.State;

    public View Render() => PreviewView(State);

    protected override Node Render(EngineerWorkspaceState state)
        => EngineerViewRenderer.RenderRoot(state, LastRejection, ActiveTransition, TryBrainOrNull());

    public async Task OnNavigatedTo(NavigationArrival<StudioNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var next = !string.IsNullOrWhiteSpace(arrival.Args?.FlowId)
            ? await SyncFromLiveFlowAsync(State, arrival.Args!.FlowId!.Trim())
            : await SyncLatestOrActiveFlowAsync(State);

        next = next.ApplyArrival(arrival.Args);
        next = await SyncLiveRepositoryReviewAsync(next);
        await ShowAndSyncAsync(next);
    }

    public Rule CanNavigateFrom(NavigationDeparture departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return State.CanDepart(departure.Force);
    }

    public Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return Task.CompletedTask;
    }

    private BrainId? TryBrainOrNull()
    {
        try { return Id.Brain; }
        catch (InvalidOperationException) { return null; }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        await TryFollowUiNeuronsAsync();
        var synced = await SyncLatestOrActiveFlowAsync(State);
        synced = await SyncLiveRepositoryReviewAsync(synced);
        if (synced != State)
        {
            await Show(synced);
        }

        await SyncUiNeuronsAsync(State);
    }

    [On]
    public async Task Open(EngineerOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = !string.IsNullOrWhiteSpace(request.FlowId)
            ? await SyncFromLiveFlowAsync(State, request.FlowId!.Trim())
            : await SyncLatestOrActiveFlowAsync(State);

        if (!string.IsNullOrWhiteSpace(request.Tab))
        {
            next = next.SelectTab(request.Tab);
        }

        next = await SyncLiveRepositoryReviewAsync(next);
        await ShowAndSyncAsync(next);
    }

    [On]
    public async Task NewRun(EngineerNewRun request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.StartNewRun(request.Goal ?? State.Goal, request.InstanceCount ?? State.InstanceCount);
        if (!string.IsNullOrWhiteSpace(request.Repository) && RepositoryId.TryParse(request.Repository, out var parsedRepo))
        {
            next = next with { RepositoryId = parsedRepo!.Value };
            await FollowRepositoryNeuronsAsync(next.RepositoryId);
        }

        if (!string.IsNullOrWhiteSpace(request.Backend))
        {
            next = next.SetParallelInstances(next.InstanceCount, request.Backend);
        }

        // Start a real durable flow.engineer instance when IFlows and registered repository are present on the brain.
        next = await TryStartDurableEngineerFlowAsync(next);
        next = await SyncLiveRepositoryReviewAsync(next);
        await ShowAndSyncAsync(next);
    }

    [On]
    public async Task Brainstorm(EngineerBrainstorm request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var goal = string.IsNullOrWhiteSpace(request.Prompt) ? State.Goal : request.Prompt.Trim();
        var planner = _services?.GetService<IEngineerPlanner>();
        var plan = planner is not null
            ? await planner.PlanAsync(State.RepositoryId, goal, [".cs", ".dart", ".md"], 5)
            : new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: $"Decompose '{goal}' into isolated parallel worktrees with Roslyn C# AST validation and aligned Dart mirrors.",
                CriticReview: "Verify Triple Git Lock, zero compiler warnings, zero 4xx/5xx live probes, and human SHA-bound PR approval.",
                Transcript:
                [
                    new("User", "user", "Goal & Scope", goal),
                    new("Architect", "architect", "DDD & Worktree Plan", $"Partition '{goal}' across {State.InstanceCount} isolated {State.BackendMode} worktree(s)."),
                    new("Critic", "critic", "Rules & Live Verification", "Enforce 5-file ChangePolicy budget, Release build, and Playwright semantics health."),
                ],
                Tasks:
                [
                    new("task-1-csharp", $"Implement C# changes for '{Truncate(goal, 36)}'", "csharp", ["src/Modules/Catalog/Flows/EngineerFlow.cs"], [], goal),
                    new("task-2-verify", "Verify Dart mirrors & live probes", "dart", ["ui/lib/src/ui/stepper/stepper_widget.dart"], ["task-1-csharp"], "Ensure C#/Dart wire alignment."),
                ]);

        var next = State with
        {
            Goal = goal,
            HasPlan = true,
            PlanApproved = false,
            CurrentStage = "brainstorm-and-plan",
            ActiveTab = "plan",
            Tasks = plan.Tasks,
            BrainstormTurns = plan.Transcript,
            StatusBanner = $"Brainstorm complete · Compiled {plan.Tasks.Length} rule-checked task(s) with User, Architect, and Critic.",
        };
        await ShowAndSyncAsync(next);
    }

    [On]
    public async Task ApprovePlan(EngineerApprovePlan request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State.AwaitingPlanRatification
            && !string.IsNullOrWhiteSpace(State.ActiveFlowId)
            && Flows is { } flows)
        {
            try
            {
                var ratified = await flows.RatifyEngineerPlan(
                    State.ActiveFlowId!,
                    State.EffectiveCleanupTargets,
                    State.EffectiveSlices);
                if (!string.IsNullOrWhiteSpace(ratified.FlowId))
                {
                    var synced = ApplyLiveSummary(State, ratified, preserveActiveTab: false, preferredFilePath: null);
                    await ShowAndSyncAsync(synced);
                    return;
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Live RatifyEngineerPlan skipped for {FlowId}.", State.ActiveFlowId);
            }
        }

        await ApplyDecisionAsync(State.ApprovePlan());
        await SyncUiNeuronsAsync(State);
    }

    [On]
    public async Task RunVerify(EngineerRunVerify request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.CompleteVerification());
        await SyncUiNeuronsAsync(State);
    }

    [On]
    public async Task ApprovePr(EngineerApprovePr request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = State.ApproveAndPublishPr();
        await ApplyDecisionAsync(decision);
        if (decision.IsAccepted)
        {
            var synced = await SyncLiveRepositoryReviewAsync(State);
            if (synced != State)
            {
                await Show(synced);
            }
        }

        await SyncUiNeuronsAsync(State);
    }

    [On]
    public async Task CleanWorktree(EngineerCleanWorktree request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.CleanWorktrees());
        await SyncUiNeuronsAsync(State);
    }

    public Task NewRunCommand() => NewRun(new EngineerNewRun());

    public Task ApprovePlanCommand() => ApprovePlan(new EngineerApprovePlan());

    public Task RunVerifyCommand() => RunVerify(new EngineerRunVerify());

    public Task ApprovePrCommand() => ApprovePr(new EngineerApprovePr());

    public Task CleanWorktreeCommand() => CleanWorktree(new EngineerCleanWorktree());

    public Task SetInstancesCommand(int count, string backend)
        => ShowAndSyncAsync(State.SetParallelInstances(count, backend));

    public Task SelectCandidateCommand(int candidateIndex)
        => SelectCandidate(new EngineerSelectCandidate(candidateIndex));

    public Task SelectFileCommand(string filePath)
        => ShowAndSyncAsync(State.SelectChangedFile(filePath));

    public Task SelectTabCommand(string tabId)
        => SelectTab(new EngineerSelectTab(tabId));

    public Task SelectStageCommand(string stageId)
        => ShowAndSyncAsync(State.SelectStage(stageId));

    public Task ToggleViewportPeekCommand()
        => ShowAndSyncAsync(State.ToggleViewportPeek());

    public Task ReplyToReviewCommentCommand(long commentId, string body)
        => ReplyReview(new EngineerReplyReview(State.LivePullRequestNumber ?? 0, commentId, body));

    [On]
    public async Task ReplyReview(EngineerReplyReview request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prNumber = request.PullRequestNumber > 0 ? request.PullRequestNumber : (State.LivePullRequestNumber ?? TryParsePullRequestNumber(State.PullRequestUrl) ?? 0);
        if (prNumber <= 0 || request.CommentId <= 0 || string.IsNullOrWhiteSpace(request.Body))
        {
            return;
        }

        var trimmedBody = request.Body.Trim();
        var (repo, _) = await TryResolveConnectedRepositoryAsync(State.RepositoryId);
        if (repo is not null)
        {
            try
            {
                await repo.ReplyToReviewComment(new ReplyReviewComment(prNumber, request.CommentId, trimmedBody));
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Live ReplyToReviewComment skipped or failed for PR #{PrNumber} comment {CommentId}.", prNumber, request.CommentId);
                return;
            }
        }

        await ShowAndSyncAsync(State.MarkReviewCommentReplied(request.CommentId, trimmedBody));
    }

    [On]
    public async Task OnPullRequestChanged(PullRequestChanged changed)
    {
        if (changed is null || changed.Number <= 0)
        {
            return;
        }

        var synced = await SyncLiveRepositoryReviewAsync(State, changed.Number, changed);
        if (synced != State)
        {
            await ShowAndSyncAsync(synced);
        }
    }

    [On]
    public async Task OnOperationProgressed(OperationProgressed progressed)
    {
        if (progressed is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(State.ActiveFlowId)
            && !string.IsNullOrWhiteSpace(progressed.FlowId)
            && !string.Equals(State.ActiveFlowId, progressed.FlowId, StringComparison.Ordinal))
        {
            return;
        }

        await ShowAndSyncAsync(State.ApplyOperationProgress(progressed));
    }

    [On]
    public async Task SelectTab(EngineerSelectTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.SelectTab(request.Tab);
        if (next.ActiveTab == "pr")
        {
            next = await SyncLiveRepositoryReviewAsync(next);
        }

        await ShowAndSyncAsync(next);
    }

    [On]
    public async Task SelectCandidate(EngineerSelectCandidate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = State.PromoteCandidateCore(request.CandidateIndex, request.FilePath);
        await ApplyDecisionAsync(decision);
        if (decision.IsAccepted
            && !string.IsNullOrWhiteSpace(State.ActiveFlowId)
            && Flows is { } flows)
        {
            try
            {
                var summary = await flows.SelectEngineerCandidate(State.ActiveFlowId!, request.CandidateIndex);
                if (!string.IsNullOrWhiteSpace(summary.FlowId))
                {
                    var synced = ApplyLiveSummary(State, summary, preserveActiveTab: true, preferredFilePath: request.FilePath);
                    await Show(synced);
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Live SelectEngineerCandidate skipped or unavailable for {FlowId}.", State.ActiveFlowId);
            }
        }

        await SyncUiNeuronsAsync(State);
    }

    [On]
    public async Task SelectInstance(EngineerSelectInstance request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idx = Math.Clamp(request.InstanceIndex, 0, Math.Max(0, State.Candidates.Length - 1));
        if (!State.ValidCandidateSelection(idx).Broken)
        {
            await SelectCandidate(new EngineerSelectCandidate(idx, request.FilePath));
            return;
        }

        var file = string.IsNullOrWhiteSpace(request.FilePath) ? State.SelectedFilePath : request.FilePath.Trim();
        var lang = file.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart" : "csharp";
        await ShowAndSyncAsync(State with
        {
            SelectedInstanceIndex = idx,
            SelectedFilePath = file,
            SelectedLanguage = lang,
            ActiveTab = State.ActiveTab is "code" or "verify" or "pr" ? State.ActiveTab : "code",
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Inspecting worktree instance w{idx + 1} (#{idx}) · {file}"),
        });
    }

    [On]
    public Task OnCommandBarInvoked(CommandBarInvoked invoked)
    {
        ArgumentNullException.ThrowIfNull(invoked);
        return invoked.ItemId switch
        {
            "new-run" => NewRun(new EngineerNewRun()),
            "brainstorm" => Brainstorm(new EngineerBrainstorm()),
            "approve-plan" => ApprovePlan(new EngineerApprovePlan()),
            "run-verify" => RunVerify(new EngineerRunVerify()),
            "approve-pr" => ApprovePr(new EngineerApprovePr()),
            "clean-worktree" => CleanWorktree(new EngineerCleanWorktree()),
            _ => Task.CompletedTask,
        };
    }

    [On]
    public Task OnStepperChanged(StepperChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if ((changed.Version > 0 && changed.Version <= _syncedStepperVersion) || string.IsNullOrWhiteSpace(changed.ActiveStepId))
        {
            return Task.CompletedTask;
        }

        var normalized = EngineerWorkspaceState.NormalizeStage(changed.ActiveStepId);
        return normalized == State.CurrentStage
            ? Task.CompletedTask
            : ShowAndSyncAsync(State.SelectStage(normalized));
    }

    [On]
    public Task OnTabChanged(TabChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if ((changed.Version > 0 && changed.Version <= _syncedTabsVersion) || string.IsNullOrWhiteSpace(changed.SelectedId))
        {
            return Task.CompletedTask;
        }

        var normalized = EngineerWorkspaceState.NormalizeTab(changed.SelectedId);
        return normalized == State.ActiveTab
            ? Task.CompletedTask
            : ShowAndSyncAsync(State.SelectTab(normalized));
    }

    [On]
    public Task OnTreeViewChanged(TreeViewChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if ((changed.Version > 0 && changed.Version <= _syncedTreeVersion) || string.IsNullOrWhiteSpace(changed.SelectedId))
        {
            return Task.CompletedTask;
        }

        var id = changed.SelectedId;
        if (id.StartsWith("wt-", StringComparison.Ordinal)
            && int.TryParse(id["wt-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wtIdx))
        {
            var defaultFile = State.Candidates.ElementAtOrDefault(wtIdx)?.ChangedFiles.FirstOrDefault() ?? State.SelectedFilePath;
            return wtIdx == State.SelectedInstanceIndex && string.Equals(defaultFile, State.SelectedFilePath, StringComparison.Ordinal)
                ? Task.CompletedTask
                : SelectInstance(new EngineerSelectInstance(wtIdx, defaultFile));
        }

        var sep = id.IndexOf(':', StringComparison.Ordinal);
        if (sep > 0 && id.StartsWith("file-", StringComparison.Ordinal)
            && int.TryParse(id["file-".Length..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileWtIdx))
        {
            var file = id[(sep + 1)..];
            return fileWtIdx == State.SelectedInstanceIndex && string.Equals(file, State.SelectedFilePath, StringComparison.Ordinal)
                ? Task.CompletedTask
                : SelectInstance(new EngineerSelectInstance(fileWtIdx, file));
        }

        return Task.CompletedTask;
    }

    [On]
    public Task OnChatSubmitted(ChatMessageSubmitted submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        return string.IsNullOrWhiteSpace(submitted.Text)
            ? Task.CompletedTask
            : Brainstorm(new EngineerBrainstorm(submitted.Text));
    }

    [On]
    public async Task OnFlowsChanged(FlowsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (string.IsNullOrWhiteSpace(changed.FlowId))
        {
            return;
        }

        var matchesActive = State.ActiveFlowId is { } active && string.Equals(changed.FlowId, active, StringComparison.Ordinal);
        var isEngineerFlow = changed.FlowId.Contains("/" + IFlows.EngineerFlow + "/", StringComparison.Ordinal);
        if (matchesActive || isEngineerFlow)
        {
            var synced = await SyncFromLiveFlowAsync(State, changed.FlowId);
            if (synced != State)
            {
                await ShowAndSyncAsync(synced);
            }
        }
    }

    private async Task ShowAndSyncAsync(EngineerWorkspaceState next)
    {
        await Show(next);
        await SyncUiNeuronsAsync(next);
    }

    private async Task TryFollowUiNeuronsAsync()
    {
        await TryFollowOneAsync<ICommandBar, CommandBarInvoked>(CommandBarKey);
        await TryFollowOneAsync<IStepper, StepperChanged>(StepperKey);
        await TryFollowOneAsync<ITabs, TabChanged>(WorkspaceTabsKey);
        await TryFollowOneAsync<ITreeView, TreeViewChanged>(WorktreeTreeKey);
        await TryFollowOneAsync<IChatThread, ChatMessageSubmitted>(BrainstormChatKey);
        await TryFollowOneAsync<IFlows, FlowsChanged>(FlowsKey);
        await TryFollowOneAsync<IFlows, OperationProgressed>(FlowsKey);
        await FollowRepositoryNeuronsAsync(State.RepositoryId);
    }

    private async Task FollowRepositoryNeuronsAsync(string? repositoryId)
    {
        if (TryBrainOrNull() is null || MainRepository is null)
        {
            return;
        }

        var slug = string.IsNullOrWhiteSpace(repositoryId) ? "main" : repositoryId.Trim();
        await TryFollowOneAsync<IRepository, PullRequestChanged>($"connection/github/{slug}");
        if (!string.Equals(slug, "main", StringComparison.OrdinalIgnoreCase))
        {
            await TryFollowOneAsync<IRepository, PullRequestChanged>("connection/github/main");
        }
    }

    private async Task<(IRepository? Repository, RepositoryView? View)> TryResolveConnectedRepositoryAsync(string? repositoryId)
    {
        if (TryBrainOrNull() is null || MainRepository is not { } mainRepo)
        {
            return (null, null);
        }

        var slug = string.IsNullOrWhiteSpace(repositoryId) ? "main" : repositoryId.Trim();
        string[] candidateKeys = string.Equals(slug, "main", StringComparison.OrdinalIgnoreCase)
            ? ["connection/github/main"]
            : [$"connection/github/{slug}", "connection/github/main"];

        foreach (var key in candidateKeys)
        {
            var repo = string.Equals(key, "connection/github/main", StringComparison.Ordinal)
                ? mainRepo
                : Repositories.TryGet(key);
            if (repo is null)
            {
                continue;
            }

            try
            {
                var view = await repo.Read();
                if (view.Connected)
                {
                    return (repo, view);
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Optional GitHub repository read skipped for {Key}.", key);
            }
        }

        return (null, null);
    }


    private static EngineerWorkspaceState ApplyLiveSummary(
        EngineerWorkspaceState basis,
        EngineerRunSummary summary,
        bool preserveActiveTab,
        string? preferredFilePath)
    {
        var stage = EngineerWorkspaceState.NormalizeStage(summary.CurrentStage);
        var plannedTargets = summary.Plan?.Tasks
            .SelectMany(static t => t.TargetFiles ?? [])
            .Where(static f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        var candidates = summary.Candidates is { Length: > 0 } liveCandidates
            ? liveCandidates.Select(c =>
            {
                var files = c.ChangedFiles is { Length: > 0 } cf
                    ? cf
                    : plannedTargets.Length > 0
                        ? plannedTargets
                        : [basis.SelectedFilePath];
                return new EngineerCandidateSummary(
                    InstanceIndex: c.InstanceIndex,
                    LeaseId: c.LeaseId,
                    Backend: c.Backend,
                    BranchName: c.BranchName,
                    Status: c.Status,
                    ChangedFiles: files,
                    CommitSha: c.Score.CommitSha,
                    Strategy: c.Strategy,
                    StrategyLabel: c.StrategyLabel,
                    TotalScore: c.Score.TotalScore,
                    ChangedLines: c.Score.ChangedLines,
                    BuildPassed: c.Score.BuildPassed,
                    TestsPassed: c.Score.TestsPassed,
                    LiveVerifyPassed: c.Score.LiveVerifyPassed,
                    ZeroTelemetryErrors: c.Score.ZeroTelemetryErrors,
                    UnifiedDiff: c.Score.UnifiedDiff,
                    Summary: c.Score.Summary,
                    Rank: c.Rank,
                    Harness: c.Harness,
                    DeletedFiles: c.DeletedFiles);
            }).ToArray()
            : summary.Instances.Length > 0
                ? summary.Instances.Select(inst =>
                {
                    var files = inst.ChangedFiles is { Length: > 0 } cf
                        ? cf
                        : plannedTargets.Length > 0
                            ? plannedTargets
                            : [basis.SelectedFilePath];
                    return new EngineerCandidateSummary(
                        InstanceIndex: inst.InstanceIndex,
                        LeaseId: inst.LeaseId,
                        Backend: inst.Backend,
                        BranchName: inst.BranchName,
                        Status: inst.Status,
                        ChangedFiles: files,
                        CommitSha: inst.CommitSha,
                        Strategy: inst.Strategy,
                        StrategyLabel: EngineerCandidateStrategies.LabelFor(inst.Strategy, inst.InstanceIndex),
                        TotalScore: inst.TotalScore,
                        ChangedLines: inst.ChangedLines,
                        BuildPassed: inst.BuildSucceeded,
                        TestsPassed: inst.BuildSucceeded,
                        LiveVerifyPassed: inst.LiveSucceeded,
                        ZeroTelemetryErrors: inst.LiveSucceeded,
                        UnifiedDiff: inst.UnifiedDiff,
                        Summary: inst.FailureDetail ?? string.Empty,
                        Rank: inst.Rank,
                        Harness: inst.Harness,
                        DeletedFiles: inst.DeletedFiles);
                }).ToArray()
                : basis.Candidates;

        var clampedCount = Math.Clamp(summary.ParallelInstances, 1, 8);
        var selectedIdx = Math.Clamp(summary.SelectedCandidateIndex, 0, Math.Max(0, candidates.Length - 1));
        var activeCandidate = candidates.ElementAtOrDefault(selectedIdx);
        var selectedFile = !string.IsNullOrWhiteSpace(preferredFilePath)
            ? preferredFilePath!.Trim()
            : activeCandidate?.ChangedFiles.Contains(basis.SelectedFilePath, StringComparer.Ordinal) == true
                ? basis.SelectedFilePath
                : activeCandidate?.ChangedFiles.FirstOrDefault() ?? basis.SelectedFilePath;
        var selectedLang = selectedFile.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart" : "csharp";
        var mappedTab = stage switch
        {
            "brainstorm-and-plan" => "plan",
            "prepare-worktree" or "implement-tasks" => "code",
            "build-and-test" or "live-verify" => "verify",
            _ => "pr",
        };
        var activeTab = preserveActiveTab
            ? basis.ActiveTab
            : (!string.Equals(basis.ActiveFlowId, summary.FlowId, StringComparison.Ordinal)
                || !string.Equals(basis.CurrentStage, stage, StringComparison.Ordinal)
                ? mappedTab
                : basis.ActiveTab);

        var shortSha = activeCandidate?.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";
        var banner = preserveActiveTab && activeCandidate is not null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Promoted candidate w{selectedIdx + 1} ({activeCandidate.StrategyLabel}) · Score {activeCandidate.TotalScore} · Commit {shortSha} · Plan {summary.ConfirmationPlanId ?? "pending"}")
            : $"Live flow {summary.FlowId} · {summary.Status} ({stage}) · Candidate w{selectedIdx + 1}";

        return basis with
        {
            ActiveFlowId = summary.FlowId,
            ActiveRunId = string.IsNullOrWhiteSpace(summary.RunId) ? basis.ActiveRunId : summary.RunId,
            RepositoryId = string.IsNullOrWhiteSpace(summary.RepoSlug) ? basis.RepositoryId : summary.RepoSlug,
            Goal = string.IsNullOrWhiteSpace(summary.Goal) ? basis.Goal : summary.Goal,
            InstanceCount = clampedCount,
            BackendMode = EngineerWorkspaceState.NormalizeBackendMode(summary.Backend, basis.BackendMode),
            CurrentStage = stage,
            ActiveTab = activeTab,
            SelectedInstanceIndex = selectedIdx,
            SelectedFilePath = selectedFile,
            SelectedLanguage = selectedLang,
            HasPlan = summary.Plan is not null || basis.HasPlan,
            PlanApproved = summary.CompletedStages.Contains("prepare-worktree", StringComparer.Ordinal) || basis.PlanApproved,
            VerificationPassed = summary.CompletedStages.Contains("live-verify", StringComparer.Ordinal) || basis.VerificationPassed,
            PrPublished = summary.Status == FlowStatus.Completed || !string.IsNullOrWhiteSpace(summary.PushedRef),
            WorktreesCleaned = summary.CompletedStages.Contains("clean", StringComparer.Ordinal),
            Tasks = summary.Plan?.Tasks is { Length: > 0 } planTasks ? planTasks : basis.Tasks,
            BrainstormTurns = summary.Plan?.Transcript is { Length: > 0 } turns ? turns : basis.BrainstormTurns,
            Candidates = candidates,
            DiagnosticsAndTraces = summary.DiagnosticsAndTraces is { Length: > 0 } traces ? traces : basis.DiagnosticsAndTraces,
            PullRequestUrl = summary.PullRequestUrl ?? basis.PullRequestUrl,
            ConfirmationPlanId = summary.ConfirmationPlanId ?? basis.ConfirmationPlanId,
            Harness = summary.Harness,
            AwaitingPlanRatification = summary.AwaitingPlanRatification,
            CleanupTargets = summary.RatifiedPlan?.CleanupTargets is { Length: > 0 } rClean
                ? rClean
                : summary.CleanupTargets is { Length: > 0 } sClean
                    ? sClean
                    : basis.CleanupTargets,
            Slices = summary.RatifiedPlan?.Slices is { Length: > 0 } rSlices
                ? rSlices
                : summary.Slices is { Length: > 0 } sSlices
                    ? sSlices
                    : basis.Slices,
            Collaborators = summary.Collaborators is { Length: > 0 } collabs
                ? collabs
                : basis.Collaborators,
            ActiveTraceId = TryExtractTraceId(summary.DiagnosticsAndTraces) ?? basis.ActiveTraceId,
            StatusBanner = banner,
        };
    }

    private static string? TryExtractTraceId(string[]? traces)
    {
        if (traces is not { Length: > 0 })
        {
            return null;
        }

        foreach (var line in traces)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foreach (var token in line.Split([' ', ':', '=', '(', ')', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 32 && token.All(char.IsAsciiHexDigit))
                {
                    return token.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}
