/*
# IntoChat Engineer
Role: Studio
Shipped: true
Operations: open, new-run, brainstorm, approve-plan, run-verify, approve-pr, clean-worktree, select-tab, select-instance, ui.command

Autonomous multi-worktree engineering workbench (`app.engineer`) orchestrating `flow.engineer` across 1..N isolated
local git worktrees or cloud sandbox volumes with User + Architect + Critic brainstorming, Roslyn C# + Dart code edits,
Release build/test + live Playwright/MCP verification, and SHA-bound human PR approval.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
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

[Neuron("app.engineer")]
[Screen("app.engineer", Title = "IntoChat Engineer", Icon = "workflow")]
public sealed class EngineerApp : AggregateScreen<EngineerWorkspaceState>
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
            Description: "Autonomous multi-worktree engineering workbench: brainstorm, plan, Roslyn/Dart code, live verify, and PR review.",
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

    public new EngineerWorkspaceState State => base.State;

    public View Render() => PreviewView(State);

    protected override Node Render(EngineerWorkspaceState state)
        => EngineerViewRenderer.RenderRoot(state, LastRejection);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        await TryFollowUiNeuronsAsync();
        var synced = await SyncLatestOrActiveFlowAsync(State);
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
        }

        if (!string.IsNullOrWhiteSpace(request.Backend))
        {
            next = next.SetParallelInstances(next.InstanceCount, request.Backend);
        }

        // Start a real durable flow.engineer instance when IFlows and registered repository are present on the brain.
        next = await TryStartDurableEngineerFlowAsync(next);
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
        await ApplyDecisionAsync(State.ApproveAndPublishPr());
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

    [On]
    public async Task SelectTab(EngineerSelectTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ShowAndSyncAsync(State.SelectTab(request.Tab));
    }

    [On]
    public async Task SelectInstance(EngineerSelectInstance request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idx = Math.Clamp(request.InstanceIndex, 0, Math.Max(0, State.Candidates.Length - 1));
        var file = string.IsNullOrWhiteSpace(request.FilePath) ? State.SelectedFilePath : request.FilePath.Trim();
        var lang = file.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart" : "csharp";
        await ShowAndSyncAsync(State with
        {
            SelectedInstanceIndex = idx,
            SelectedFilePath = file,
            SelectedLanguage = lang,
            ActiveTab = "code",
            StatusBanner = string.Create(CultureInfo.InvariantCulture, $"Inspecting worktree instance #{idx} · {file}"),
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
        var isEngineerFlow = changed.FlowId.Contains($"/{IFlows.EngineerFlow}/", StringComparison.Ordinal);
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
    }

    private async Task TryFollowOneAsync<TContract, TSignal>(string key)
        where TContract : class
        where TSignal : Signal
    {
        try
        {
            await Follow<TContract, TSignal>(key);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Optional synapse follow for {Key} skipped in minimal standalone host.", key);
        }
    }

    private async Task<EngineerWorkspaceState> TryStartDurableEngineerFlowAsync(EngineerWorkspaceState basis)
    {
        if (_services?.GetService<IWorkTree>() is not { } wt || wt.ListRepositories().Count == 0)
        {
            return basis;
        }

        if (TryGetOptional<IFlows>(FlowsKey) is not { } flows)
        {
            return basis;
        }

        try
        {
            if (!RepositoryId.TryParse(basis.RepositoryId, out var repoId))
            {
                return basis;
            }

            _ = wt.ResolveRepository(repoId!);
            var payload = new EngineerStartRequest(
                RunId: basis.ActiveRunId,
                Repository: repoId!.Value,
                Goal: basis.Goal,
                InstanceCount: basis.InstanceCount,
                Backend: basis.BackendMode).ToStartJson();

            var flowId = await flows.Start(IFlows.EngineerFlow, Truncate(basis.Goal, 52), null, payload);
            return basis with { ActiveFlowId = flowId };
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Durable flow.engineer start skipped; interactive workbench state remains active.");
            return basis;
        }
    }

    private async Task<EngineerWorkspaceState> SyncLatestOrActiveFlowAsync(EngineerWorkspaceState basis)
    {
        if (!string.IsNullOrWhiteSpace(basis.ActiveFlowId))
        {
            return await SyncFromLiveFlowAsync(basis, basis.ActiveFlowId!);
        }

        if (TryGetOptional<IFlows>(FlowsKey) is not { } flows)
        {
            return basis;
        }

        try
        {
            var recent = await flows.List(new FlowQuery(Type: IFlows.EngineerFlow, Limit: 1));
            if (recent.Length > 0)
            {
                return await SyncFromLiveFlowAsync(basis, recent[0].FlowId);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Skipping initial flow.engineer discovery in standalone EngineerApp host.");
        }

        return basis;
    }

    private async Task<EngineerWorkspaceState> SyncFromLiveFlowAsync(EngineerWorkspaceState basis, string flowId)
    {
        if (TryGetOptional<IFlows>(FlowsKey) is not { } flows)
        {
            return basis;
        }

        try
        {
            var summary = await flows.InspectEngineerRun(flowId);
            if (!string.IsNullOrWhiteSpace(summary.FlowId) && (summary.Ok || summary.Instances.Length > 0 || summary.Plan is not null))
            {
                var stage = EngineerWorkspaceState.NormalizeStage(summary.CurrentStage);
                var plannedTargets = summary.Plan?.Tasks
                    .SelectMany(static t => t.TargetFiles ?? [])
                    .Where(static f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray() ?? [];

                var candidates = summary.Instances.Length > 0
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
                            CommitSha: inst.CommitSha);
                    }).ToArray()
                    : basis.Candidates;

                var clampedCount = Math.Clamp(summary.ParallelInstances, 1, 8);
                var selectedIdx = Math.Clamp(basis.SelectedInstanceIndex, 0, Math.Max(0, candidates.Length - 1));
                var selectedFile = candidates.ElementAtOrDefault(selectedIdx)?.ChangedFiles.FirstOrDefault() ?? basis.SelectedFilePath;
                var selectedLang = selectedFile.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart" : "csharp";
                var mappedTab = stage switch
                {
                    "brainstorm-and-plan" => "plan",
                    "prepare-worktree" or "implement-tasks" => "code",
                    "build-and-test" or "live-verify" => "verify",
                    _ => "pr",
                };
                var activeTab = !string.Equals(basis.ActiveFlowId, summary.FlowId, StringComparison.Ordinal)
                    || !string.Equals(basis.CurrentStage, stage, StringComparison.Ordinal)
                    ? mappedTab
                    : basis.ActiveTab;

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
                    StatusBanner = $"Live flow {summary.FlowId} · {summary.Status} ({stage})",
                };
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "InspectEngineerRun unavailable for {FlowId}; falling back to FlowView sync.", flowId);
        }

        try
        {
            var view = await flows.Read(flowId);
            if (view is null)
            {
                return basis;
            }

            var stage = EngineerWorkspaceState.NormalizeStage(view.Current ?? view.WaitingStep ?? view.Done.LastOrDefault());
            return basis with
            {
                ActiveFlowId = flowId,
                CurrentStage = stage,
                PlanApproved = view.Done.Contains("prepare-worktree", StringComparer.Ordinal) || basis.PlanApproved,
                VerificationPassed = view.Done.Contains("live-verify", StringComparer.Ordinal) || basis.VerificationPassed,
                PrPublished = view.Status == FlowStatus.Completed || basis.PrPublished,
                WorktreesCleaned = view.Done.Contains("clean", StringComparer.Ordinal) || basis.WorktreesCleaned,
                StatusBanner = $"Live flow {flowId} · {view.Status} ({stage})",
            };
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Could not sync flow {FlowId} into EngineerApp.", flowId);
            return basis;
        }
    }

    private async Task SyncUiNeuronsAsync(EngineerWorkspaceState state)
    {
        try
        {
            if (TryGetOptional<ICommandBar>(CommandBarKey) is { } cmdBar)
            {
                await cmdBar.Configure(
                [
                    new CommandBarItem("new-run", "New Run", "plus"),
                    new CommandBarItem("brainstorm", "Brainstorm", "sparkles"),
                    new CommandBarItem("approve-plan", "Approve Plan", "check", Disabled: state.PlanReadyToApprove().Broken),
                    new CommandBarItem("run-verify", "Run Verify", "play", Disabled: state.ReadyForVerification().Broken),
                    new CommandBarItem("approve-pr", "Approve PR", "git-pull-request", Disabled: state.ReadyForPrApproval().Broken),
                    new CommandBarItem("clean-worktree", "Clean Worktree", "trash", Primary: false, Disabled: state.HasActiveWorktrees().Broken),
                ]);
            }

            if (TryGetOptional<IStepper>(StepperKey) is { } stepper)
            {
                var stepState = await stepper.Configure(BuildStepperSteps(state), state.CurrentStage, "IntoChat-Engineer Pipeline");
                _syncedStepperVersion = Math.Max(_syncedStepperVersion, stepState.Version);
            }

            if (TryGetOptional<ITabs>(WorkspaceTabsKey) is { } tabs)
            {
                var tabState = await tabs.Configure(
                [
                    new UiTabItem("plan", "Plan"),
                    new UiTabItem("code", "Code"),
                    new UiTabItem("verify", "Verification & Traces"),
                    new UiTabItem("pr", "PR Review"),
                ], state.ActiveTab);
                _syncedTabsVersion = Math.Max(_syncedTabsVersion, tabState.Version);
            }

            if (TryGetOptional<ITreeView>(WorktreeTreeKey) is { } tree)
            {
                var nodes = new List<TreeNode>();
                var expanded = new List<string>();
                foreach (var c in state.Candidates)
                {
                    var folderId = string.Create(CultureInfo.InvariantCulture, $"wt-{c.InstanceIndex}");
                    expanded.Add(folderId);
                    nodes.Add(new TreeNode(folderId, $"#{c.InstanceIndex} · {c.Backend} ({c.Status})", IsFolder: true));
                    foreach (var file in c.ChangedFiles)
                    {
                        var fileId = string.Create(CultureInfo.InvariantCulture, $"file-{c.InstanceIndex}:{file}");
                        nodes.Add(new TreeNode(fileId, Path.GetFileName(file), ParentId: folderId));
                    }
                }

                var selectedTreeId = string.Create(CultureInfo.InvariantCulture, $"file-{state.SelectedInstanceIndex}:{state.SelectedFilePath}");
                var treeState = await tree.Configure(nodes, expandedIds: expanded, selectedId: selectedTreeId);
                _syncedTreeVersion = Math.Max(_syncedTreeVersion, treeState.Version);
            }

            if (TryGetOptional<ICodeEditor>(CodeEditorKey) is { } editor)
            {
                await editor.Configure(
                    state.SelectedFilePath,
                    state.SelectedLanguage,
                    BuildCodePreview(state),
                    readOnly: false);
            }

            if (TryGetOptional<IKanbanBoard>(KanbanKey) is { } kanban)
            {
                KanbanColumn[] cols =
                [
                    new("planned", "Planned", "list"),
                    new("active", "In Worktree", "code"),
                    new("verified", "Verified", "check"),
                ];
                var targetCol = state.VerificationPassed ? "verified" : state.PlanApproved ? "active" : "planned";
                var cards = state.Tasks.Select((t, idx) => new KanbanCard(
                    t.Id,
                    targetCol,
                    t.Title,
                    Subtitle: string.Join(", ", t.TargetFiles),
                    Tag: t.Language,
                    Order: idx)).ToArray();
                await kanban.Configure("Engineer Task Graph", cols, cards, state.Tasks.FirstOrDefault()?.Id);
            }

            if (TryGetOptional<ITable>(TasksTableKey) is { } table)
            {
                if (state.ActiveTab == "verify")
                {
                    await table.Render(
                        "Verification & Telemetry Spans",
                        [
                            new TableColumn("step", "Gate / Span"),
                            new TableColumn("detail", "Observed Outcome"),
                            new TableColumn("status", "Status"),
                        ],
                        state.DiagnosticsAndTraces.Select((line, idx) => (IReadOnlyList<string>)
                        [
                            string.Create(CultureInfo.InvariantCulture, $"gate-{idx + 1}"),
                            line,
                            state.VerificationPassed ? "PASS" : "READY",
                        ]).ToArray());
                }
                else
                {
                    await table.Render(
                        "Task Checkpoints & Roslyn Diagnostics",
                        [
                            new TableColumn("id", "Task"),
                            new TableColumn("title", "Title"),
                            new TableColumn("lang", "Language"),
                            new TableColumn("status", "Checkpoint"),
                        ],
                        state.Tasks.Select(t => (IReadOnlyList<string>)
                        [
                            t.Id,
                            t.Title,
                            t.Language,
                            state.VerificationPassed ? "verified" : state.PlanApproved ? "checkpointed" : "planned",
                        ]).ToArray());
                }
            }

            if (TryGetOptional<IChatThread>(BrainstormChatKey) is { } chat)
            {
                var chatSig = string.Create(CultureInfo.InvariantCulture, $"{state.ActiveRunId}:{state.BrainstormTurns.Length}:{state.Goal}");
                if (_lastSyncedChatSignature != chatSig)
                {
                    if (_lastSyncedChatSignature is null && (await chat.Read()).Version == 0)
                    {
                        await chat.Configure(
                            "engineer-brainstorm",
                            "Architect · Critic · User Planning",
                            "claude-sonnet-4-5",
                            [
                                new ChatModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "Anthropic"),
                                new ChatModelOption("gpt-5-thinking", "GPT-5 Thinking", "OpenAI"),
                                new ChatModelOption("qwen2.5-coder:14b", "Qwen 2.5 Coder (Local)", "Ollama"),
                            ]);
                    }

                    for (var i = 0; i < state.BrainstormTurns.Length; i++)
                    {
                        var turn = state.BrainstormTurns[i];
                        var role = turn.Role == "user" ? "user" : "assistant";
                        var msgId = string.Create(CultureInfo.InvariantCulture, $"{state.ActiveRunId}-turn-{i}");
                        await chat.AppendMessage(new ChatMessage(
                            Id: msgId,
                            Role: role,
                            Text: $"**[{turn.Speaker} · {turn.Perspective}]** {turn.Message}",
                            Status: "done"));
                    }

                    _lastSyncedChatSignature = chatSig;
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "EngineerApp UI neuron sync skipped in minimal test host.");
        }
    }

    private static StepperStep[] BuildStepperSteps(EngineerWorkspaceState state)
    {
        var activeIdx = Array.IndexOf(EngineerWorkspaceState.PipelineStageIds, state.CurrentStage);
        if (activeIdx < 0) { activeIdx = 0; }

        StepperStep Make(int idx, string id, string title, string icon)
        {
            var status = state.PrPublished || idx < activeIdx
                ? StepperStatuses.Done
                : idx == activeIdx
                    ? StepperStatuses.Active
                    : StepperStatuses.Pending;
            return new StepperStep(id, title, status, Icon: icon);
        }

        return
        [
            Make(0, "brainstorm-and-plan", "Brainstorm & Plan", "sparkles"),
            Make(1, "prepare-worktree", "Prepare Worktree", "git-branch"),
            Make(2, "implement-tasks", "Implement Tasks", "code"),
            Make(3, "build-and-test", "Build & Test", "hammer"),
            Make(4, "live-verify", "Live Verify", "globe"),
            Make(5, "review-and-pr", "Review & PR", "git-pull-request"),
        ];
    }

    private static string BuildCodePreview(EngineerWorkspaceState state)
    {
        if (state.SelectedLanguage == "dart")
        {
            return """
                // Aligned Flutter semantics widget inside isolated worktree
                class StepperStageBadge extends StatelessWidget {
                  const StepperStageBadge({super.key, required this.stage, required this.status});

                  final String stage;
                  final String status;

                  @override
                  Widget build(BuildContext context) {
                    return Semantics(
                      identifier: 'neuron:ui.stepper:engineer/stepper',
                      container: true,
                      child: Text('$stage · $status'),
                    );
                  }
                }
                """;
        }

        return $$"""
            // Worktree #{{state.SelectedInstanceIndex}} ({{state.BackendMode}}) · {{state.SelectedFilePath}}
            namespace IntoChat.OS.Modules.Apps.Flows;

            [DigitalBrain.Neurons.Neuron(IFlows.EngineerFlow)]
            internal sealed class EngineerFlow : Flow<EngineerRun>, IFlowStarter
            {
                // Goal: {{state.Goal}}
                // Parallel worktrees: {{state.InstanceCount}} · Verified: {{state.VerificationPassed}}
            }
            """;
    }

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "{Service} is optional when EngineerApp runs in standalone tests.", typeof(T).Name);
            return null;
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}
