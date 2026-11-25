/*
# Tasks
Role: Default
Shipped: true
Operations: open, view, filter, select, new, cancel, relate-kind, relate, show-source, ask-ino, move, advance, complete, clear-done, approve-issue-draft, dismiss-issue-draft, escalate-issue

Workspace task planning & live brain flow orchestration (`app.tasks`) built on `AggregateScreen<TasksWorkspaceState>`
and `control.*` children (`IKanbanControl`, `IStepperControl`, `ITabStripControl`, `IQueryTableControl`).
*/
using IntoChat.OS.Host.Apps;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;
using CardMoved = DigitalBrain.UI.Commands.CardMoved;
using RowSelected = DigitalBrain.UI.Commands.RowSelected;
using SelectionChanged = DigitalBrain.UI.Commands.SelectionChanged;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Visuals;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Tasks;

[Signal("tasks.open")]
public sealed record TasksOpen(string? Input = null, string? FlowId = null, string? View = null) : Signal;

public sealed record PendingIssueTriageDraft(
    string ConnectionKey,
    string Repository,
    string PreviewId,
    int IssueNumber,
    string Body,
    string[] SuggestedLabels,
    DateTimeOffset ExpiresAt);

[Signal("tasks.view")]
public sealed record TasksView(string? View = null) : Signal;

[Signal("tasks.filter")]
public sealed record TasksFilter(string? Owner = null, string? Status = null, string? Type = null) : Signal;

[Signal("tasks.select")]
public sealed record TasksSelect(string? FlowId = null) : Signal;

[Signal("tasks.new")]
public sealed record TasksNew(string? Title = null, string? Description = null, string? Stage = null) : Signal;

[Signal("tasks.cancel")]
public sealed record TasksCancel(string? FlowId = null, string? Reason = null) : Signal;

[Signal("tasks.relate-kind")]
public sealed record TasksRelateKind(string? Kind = null) : Signal;

[Signal("tasks.relate")]
public sealed record TasksRelate(string? FlowId = null, string? Kind = null, string? Target = null, string? Lag = null) : Signal;

[Signal("tasks.show-source")]
public sealed record TasksShowSource(string? FlowId = null) : Signal;

[Signal("tasks.ask-ino")]
public sealed record TasksAskIno(string? FlowId = null) : Signal;

[Signal("tasks.move")]
public sealed record TasksMove(string? TaskId = null, string? Stage = null) : Signal;

[Signal("tasks.advance")]
public sealed record TasksAdvance : Signal;

[Signal("tasks.complete")]
public sealed record TasksComplete : Signal;

[Signal("tasks.clear-done")]
public sealed record TasksClearDone : Signal;

[Signal("tasks.approve-issue-draft")]
public sealed record TasksApproveIssueDraft(string? ConnectionKey = null, int IssueNumber = 0, string? PreviewId = null) : Signal;

[Signal("tasks.dismiss-issue-draft")]
public sealed record TasksDismissIssueDraft(string? ConnectionKey = null, string? PreviewId = null) : Signal;

[Signal("tasks.escalate-issue")]
public sealed record TasksEscalateIssue(string? ConnectionKey = null, string? Repository = null, int IssueNumber = 0, string? PreviewId = null) : Signal;

[Signal("tasks.add-task")]
public sealed record TasksAddTask : Signal;

[Signal("tasks.toggle-status-filter")]
public sealed record TasksToggleStatusFilter(string? Status = null) : Signal;

[Signal("tasks.toggle-owner-filter")]
public sealed record TasksToggleOwnerFilter(string? Owner = null) : Signal;

/// <summary>Keeps what a person types into one of the quick-task fields ("title", "description", "lag"); the text is the value.</summary>
[Signal("tasks.edit-draft")]
public sealed record TasksEditDraft(string? Field = null, string? Value = null) : Signal;

[Neuron("app.tasks")]
[Screen("app.tasks", Title = "Tasks", Icon = "tasks", Category = "Workspace")]
public sealed partial class TasksApp : AggregateScreen<TasksWorkspaceState>, INavigatedTo<TasksNavArgs>, INavigatedFrom
{
    public const string AppName = "tasks";
    public const string AppOwner = "app.tasks";

    // The keys of Tasks' control children, each at {app id}/{key}.
    public const string KanbanKey = "kanban";
    public const string StepperKey = "stepper";
    public const string TabsKey = "tabs";
    public const string TableKey = "table";

    private const string FlowsKey = "flows/index";
    private const string ApprovalsKey = "approvals/index";
    private const string SynapsesKey = "ino/synapses";

    [Slot(Key = FlowsKey, Required = false)] private partial IFlows? Flows { get; }
    [Slot(Key = IEngineerRuns.DefaultKey, Required = false)] private partial IEngineerRuns? EngineerRuns { get; }
    [Slot(Key = ApprovalsKey, Required = false)] private partial IApprovals? Approvals { get; }
    [Slot(Key = SynapsesKey, Required = false)] private partial ISynapseStudio? SynapseStudio { get; }
    [Slot(Key = IFocus.DefaultKey, Required = false)] private partial IFocus? Focus { get; }
    [Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)] private partial IConfirmationInbox? ConfirmationInbox { get; }
    [Slot(Key = "main", Required = false)] private partial IConnectionCatalog? ConnectionCatalog { get; }
    [Slot(Required = false)] private partial SlotCollection<IRepository> Repositories { get; }

    private static readonly ILogger ConsoleLogger = OsConsoleLog.For<TasksApp>();
    private readonly ILogger _logger;
    private readonly TimeProvider? _injectedTime;

    private FlowView[] _flows = [];
    private Approval[] _approvals = [];
    private ConfirmationRequested[] _confirmations = [];
    private PendingIssueTriageDraft[] _issueDrafts = [];

    public TasksApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public TasksApp(ILogger<TasksApp>? logger = null, TimeProvider? time = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
        _injectedTime = time;
    }

    public TimeProvider? Time { get; set; }

    public static AppBundle Bundle => AppBundle.FromScreen<TasksApp>(
        AppName,
        "2.0.0",
        description: "Plan and follow workspace tasks and brain flows across Kanban board, list, and stage stepper.",
        operations:
        [
            new("open", "Show the workspace Kanban board and live brain flows."),
            new("view", "Switch between board, list, and details views."),
            new("filter", "Filter by owner, status, and flow type."),
            new("select", "Select a task or brain flow to inspect its lifecycle."),
            new("new", "Start a plan flow or add a task from a title and description."),
            new("cancel", "Cancel an active brain flow."),
            new("relate-kind", "Pick the relation kind Relate applies."),
            new("relate", "Relate a flow to another."),
            new("show-source", "Show a build flow's deployed or staged unit."),
            new("ask-ino", "Point Ino at a build flow's unit to change it."),
            new("move", "Move a task to backlog, in-progress, review, or done."),
            new("advance", "Advance the selected task to the next lifecycle stage."),
            new("complete", "Mark the selected task Done."),
            new("clear-done", "Clear all completed tasks from the board."),
            new("approve-issue-draft", "Approve and post a pending GitHub issue triage comment draft."),
            new("dismiss-issue-draft", "Dismiss a pending GitHub issue triage comment draft."),
            new("escalate-issue", "Escalate a GitHub issue into an Engineer Studio run."),
        ],
        toolbar:
        [
            new("view", Icon: "kanban", Label: "Board"),
            new("new", Icon: "plus", Label: "Add Task"),
        ]);

    public new TasksWorkspaceState State => base.State;

    public View Render() => PreviewView(State);

    public View RenderSide() => RenderSideCore(State, _flows, _approvals, State.Source);

    protected override Node Render(TasksWorkspaceState state)
        => RenderRootCore(Children, state, LastRejection, _flows, _approvals, _confirmations, _issueDrafts, NowUtc(), ActiveTransition);

    public async Task OnNavigatedTo(NavigationArrival<TasksNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var next = State.ApplyArrival(arrival.Args);
        if (next != State)
        {
            await Show(next);
        }

        await RefreshFlowsAndSyncAsync();
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

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);

        await FollowChild<IKanbanControl, CardMoved>(KanbanKey);
        await FollowChild<IKanbanControl, SelectionChanged>(KanbanKey);
        await FollowChild<IStepperControl, SelectionChanged>(StepperKey);
        await FollowChild<ITabStripControl, SelectionChanged>(TabsKey);
        await FollowChild<IQueryTableControl, RowSelected>(TableKey);
        await TryFollowOptionalAsync<IFlows, FlowsChanged>(FlowsKey);

        await RefreshFlowsAndSyncAsync();
    }

    // ── App Operations ([On] Signals) ────────────────────────────────────────

    [On]
    public async Task Open(TasksOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.ApplyOpen(request.Input, request.FlowId, request.View);
        if (next != State)
        {
            await Show(next);
        }

        await RefreshFlowsAndSyncAsync();
    }

    [On]
    public async Task View(TasksView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(State.SelectTab(request.View));
    }

    [On]
    public async Task Filter(TasksFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FlowStatus? status = Enum.TryParse<FlowStatus>(request.Status, ignoreCase: true, out var parsed) ? parsed : null;
        await Show(State.ApplyFilter(request.Owner, status, request.Type));
        await RefreshFlowsAndSyncAsync();
    }

    [On]
    public async Task Select(TasksSelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(State.SelectTaskOrFlow(request.FlowId));
    }

    [On]
    public async Task New(TasksNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Blank(request.Title) ?? Blank(State.Draft.Title) ?? "New task";
        var description = request.Description?.Trim() ?? State.Draft.Description.Trim();
        var stage = TasksWorkspaceState.TryNormalizeStage(request.Stage) ?? "backlog";
        var goal = string.IsNullOrWhiteSpace(description) ? title : description;
        var inner = JsonSerializer.Serialize(new PlanInner { Goal = goal }, TasksAppJsonContext.Default.PlanInner);

        if (Flows is { } flows)
        {
            try
            {
                var flowId = await flows.Start(PlanFlow.Alias, title, null, inner);
                await Show(State.MarkStarted(flowId, title, description, stage));
                await RefreshFlowsAndSyncAsync();
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogSwallowed(error, "The plan flow could not start; recording status on TasksWorkspaceState.");
                await Show(State.WithStatus($"Could not start: {error.Message}"));
                return;
            }
        }

        await ApplyDecisionAsync(State.AddTask(title, description, stage));
    }

    [On]
    public async Task Cancel(TasksCancel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }

        try
        {
            if (Flows is not { } flows)
            {
                await Show(State.WithStatus("Could not cancel: flows index unavailable."));
                return;
            }

            await flows.Cancel(flowId, Blank(request.Reason) ?? "Cancelled from Tasks");
            await Show(State.WithStatus($"Cancelled {flowId}"));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "The cancel was refused; the status line reports why.");
            await Show(State.WithStatus($"Could not cancel: {error.Message}"));
        }

        await RefreshFlowsAndSyncAsync();
    }

    [On]
    public async Task RelateKind(TasksRelateKind request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.SetRelateKind(request.Kind);
        if (next != State)
        {
            await Show(next);
        }
    }

    [On]
    public async Task Relate(TasksRelate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId || Blank(request.Target) is not { } target) { return; }

        var kind = Enum.TryParse<RelationKind>(request.Kind ?? State.RelateKind, ignoreCase: true, out var parsed)
            ? parsed
            : RelationKind.BlockedBy;
        TimeSpan? lag = TasksWorkspaceState.ParseLag(request.Lag) is { } days
            ? days
            : TimeSpan.TryParse(request.Lag, CultureInfo.InvariantCulture, out var span) ? span : null;

        try
        {
            if (Flows is not { } flows)
            {
                await Show(State.WithStatus("Could not relate: flows index unavailable."));
                return;
            }

            await flows.Relate(flowId, new FlowRelation(target, kind, lag));
            await Show(State.WithStatus($"Related {kind}"));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "The relation was refused; the status line reports why.");
            await Show(State.WithStatus($"Could not relate: {error.Message}"));
        }

        await RefreshFlowsAndSyncAsync();
    }

    [On]
    public async Task ShowSource(TasksShowSource request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        var source = unit is null ? await StagedSourceAsync(flowId) : await SourceOfAsync(unit);
        await Show(State.WithSource(flowId, source));
    }

    [On]
    public async Task AskIno(TasksAskIno request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        var alias = unit?.Aliases.FirstOrDefault() ?? unit?.SourceKey ?? AliasOrKey(flowId);
        if (Focus is { } focus)
        {
            await focus.Point("window:ino", FocusTone.Info, $"Change unit {alias}", new FocusAction("open", "ino"));
        }
        await Show(State.WithStatus($"Asked Ino to change {alias}"));
    }

    [On]
    public async Task Move(TasksMove request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.TaskId) is not { } taskId || Blank(request.Stage) is not { } stage) { return; }
        await ApplyDecisionAsync(State.MoveTask(taskId, stage));
    }

    [On]
    public async Task Advance(TasksAdvance request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.AdvanceSelectedStage());
    }

    [On]
    public async Task Complete(TasksComplete request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.CompleteSelectedTask());
    }

    [On]
    public async Task ClearDone(TasksClearDone request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.ClearDoneTasks());
    }

    [On]
    public async Task ApproveIssueDraft(TasksApproveIssueDraft request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.PreviewId) ?? Blank(State.FocusedDraftPreviewId)) is not { } previewId)
        {
            await Show(State.WithStatus("Select a pending issue triage draft to approve."));
            return;
        }

        var matchingDraft = _issueDrafts.FirstOrDefault(d => string.Equals(d.PreviewId, previewId, StringComparison.Ordinal));
        var connKey = NormalizeGitHubConnectionKey(Blank(request.ConnectionKey) ?? matchingDraft?.ConnectionKey);
        var issueNumber = request.IssueNumber > 0 ? request.IssueNumber : (matchingDraft?.IssueNumber ?? 0);

        try
        {
            var repo = Repositories[connKey];
            var result = await repo.CommentOnIssue(new CommentOnIssueRequest(
                IssueNumber: issueNumber,
                RequestId: string.Concat("tasks-approve:", previewId),
                PreviewId: previewId));
            var labelsSummary = result.AppliedLabels is { Length: > 0 }
                ? $" (labels: {string.Join(", ", result.AppliedLabels)})"
                : string.Empty;
            var nextState = State.WithStatus(
                string.Create(CultureInfo.InvariantCulture, $"Approved & posted triage comment on issue #{result.IssueNumber}{labelsSummary}."));
            if (string.Equals(nextState.FocusedDraftPreviewId, previewId, StringComparison.Ordinal))
            {
                nextState = nextState with { FocusedDraftPreviewId = null };
            }

            await Show(nextState);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "The issue triage draft approval failed; the status line reports why.");
            await Show(State.WithStatus($"Could not approve draft: {error.Message}"));
        }

        await RefreshFlowsAndSyncAsync();
    }

    [On]
    public async Task DismissIssueDraft(TasksDismissIssueDraft request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.PreviewId) ?? Blank(State.FocusedDraftPreviewId)) is not { } previewId)
        {
            await Show(State.WithStatus("Select a pending issue triage draft to dismiss."));
            return;
        }

        var matchingDraft = _issueDrafts.FirstOrDefault(d => string.Equals(d.PreviewId, previewId, StringComparison.Ordinal));
        var connKey = NormalizeGitHubConnectionKey(Blank(request.ConnectionKey) ?? matchingDraft?.ConnectionKey);

        try
        {
            var repo = Repositories[connKey];
            var dismissed = await repo.DismissIssueDraft(previewId);
            var statusMessage = dismissed
                ? $"Dismissed issue triage draft '{previewId}'."
                : $"Issue triage draft '{previewId}' was already cleared.";
            var nextState = State.WithStatus(statusMessage);
            if (string.Equals(nextState.FocusedDraftPreviewId, previewId, StringComparison.Ordinal))
            {
                nextState = nextState with { FocusedDraftPreviewId = null };
            }

            await Show(nextState);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "Dismissing the issue triage draft failed; the status line reports why.");
            await Show(State.WithStatus($"Could not dismiss draft: {error.Message}"));
        }

        await RefreshFlowsAndSyncAsync();
    }

    [On]
    public async Task EscalateIssue(TasksEscalateIssue request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previewId = Blank(request.PreviewId) ?? Blank(State.FocusedDraftPreviewId);
        var matchingDraft = previewId is not null
            ? _issueDrafts.FirstOrDefault(d => string.Equals(d.PreviewId, previewId, StringComparison.Ordinal))
            : null;
        var connKey = NormalizeGitHubConnectionKey(Blank(request.ConnectionKey) ?? matchingDraft?.ConnectionKey);
        var issueNumber = request.IssueNumber > 0 ? request.IssueNumber : (matchingDraft?.IssueNumber ?? 0);
        var repoLabel = Blank(request.Repository) ?? Blank(matchingDraft?.Repository) ?? "os";
        if (issueNumber <= 0)
        {
            await Show(State.WithStatus("Select a GitHub issue with a valid number to escalate to Engineer Studio."));
            return;
        }

        var effectivePreviewId = previewId ?? string.Create(CultureInfo.InvariantCulture, $"issue-{issueNumber}");
        var issueTitle = string.Create(CultureInfo.InvariantCulture, $"Issue #{issueNumber}");
        var issueContext = matchingDraft?.Body ?? string.Empty;

        try
        {
            var repo = Repositories[connKey];
            var issueRead = await repo.ReadIssue(new ReadIssue(issueNumber));
            if (!string.IsNullOrWhiteSpace(issueRead.Snapshot.Title))
            {
                issueTitle = issueRead.Snapshot.Title.Trim();
            }

            if (!string.IsNullOrWhiteSpace(issueRead.Body))
            {
                issueContext = issueRead.Body.Trim();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "Reading live GitHub issue details failed; escalating with cached draft summary.");
        }

        var taskTitle = string.Create(CultureInfo.InvariantCulture, $"Fix #{issueNumber}: {issueTitle}");
        var boundedEvidence = issueContext.Length > 1200 ? issueContext[..1200] : issueContext;
        var goal = string.IsNullOrWhiteSpace(boundedEvidence)
            ? string.Create(CultureInfo.InvariantCulture, $"Fix GitHub issue #{issueNumber} in {repoLabel}: {issueTitle}")
            : string.Create(CultureInfo.InvariantCulture, $"Fix GitHub issue #{issueNumber} in {repoLabel}: {issueTitle}\n\n{boundedEvidence}");

        string escalatedFlowId;
        if (Flows is { } flows)
        {
            try
            {
                escalatedFlowId = await StartEngineerFlowForIssueAsync(flows, repoLabel, taskTitle, goal);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogSwallowed(error, "The Engineer flow could not start; recording status on TasksWorkspaceState.");
                await Show(State.WithStatus($"Could not escalate issue: {error.Message}"));
                return;
            }
        }
        else
        {
            escalatedFlowId = string.Create(CultureInfo.InvariantCulture, $"issue-{issueNumber}");
        }

        await Show(State.RecordEscalatedIssue(effectivePreviewId, repoLabel, issueNumber, escalatedFlowId, taskTitle, goal));
        await RefreshFlowsAndSyncAsync();
    }

    // ── Toolbar and quick-task reactions ──────────────────────────────────────

    [On]
    public async Task AddTask(TasksAddTask request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Blank(State.Draft.Title) ?? string.Create(CultureInfo.InvariantCulture, $"Task #{State.NextTaskSequence}");
        await ApplyDecisionAsync(State.AddTask(title, State.Draft.Description, "backlog"));
    }

    [On]
    public Task ToggleStatusFilter(TasksToggleStatusFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = State.Query.Status?.ToString();
        var nextStatus = string.Equals(current, request.Status, StringComparison.OrdinalIgnoreCase) ? null : request.Status;
        return Filter(new TasksFilter(Owner: State.Query.Owner, Status: nextStatus, Type: State.Query.Type));
    }

    [On]
    public Task ToggleOwnerFilter(TasksToggleOwnerFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextOwner = string.Equals(State.Query.Owner, request.Owner, StringComparison.Ordinal) ? null : request.Owner;
        return Filter(new TasksFilter(Owner: nextOwner, Status: State.Query.Status?.ToString(), Type: State.Query.Type));
    }

    [On]
    public async Task EditDraft(TasksEditDraft request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.Field) is not { } field) { return; }
        await Store(State.UpdateDraftField(field, request.Value));
    }

    // ── Control children: each signals the person's change; Tasks applies it and renders it back as props ──

    [On]
    public async Task OnCardMoved(CardMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);
        if (SignalChild != KanbanKey || TasksWorkspaceState.TryNormalizeStage(moved.ColumnId) is not { } stage) { return; }
        await ApplyDecisionAsync(State.MoveTask(moved.CardId, stage));
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return SignalChild switch
        {
            KanbanKey => Show(State.SelectTaskOrFlow(changed.Id)),
            StepperKey => MoveSelectedTaskAsync(changed.Id),
            TabsKey => Show(State.SelectTab(changed.Id)),
            _ => Task.CompletedTask,
        };
    }

    [On]
    public Task OnRowSelected(RowSelected selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return SignalChild == TableKey ? Show(State.SelectTaskOrFlow(selected.RowId)) : Task.CompletedTask;
    }

    // The stepper names a stage: the selected task moves there.
    private async Task MoveSelectedTaskAsync(string stepId)
    {
        if (TasksWorkspaceState.TryNormalizeStage(stepId) is not { } stage || State.SelectedTask is not { } selected
            || string.Equals(selected.Stage, stage, StringComparison.Ordinal))
        {
            return;
        }

        await ApplyDecisionAsync(State.MoveTask(selected.Id, stage));
    }

    [On]
    public Task OnFlowsChanged(FlowsChanged? changed)
        => changed is null ? Task.CompletedTask : RefreshFlowsAndSyncAsync();

    // ── Live Synchronization with Brain Flows ────────────────────────────────

    private async Task RefreshFlowsAndSyncAsync()
    {
        if (Flows is { } flows)
        {
            try
            {
                _flows = await flows.List(State.Query);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogSwallowed(error, "The flows index is optional; the board shows workspace tasks and last read flows.");
            }
        }

        if (Approvals is { } approvals)
        {
            try
            {
                _approvals = [.. await approvals.Pending()];
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogSwallowed(error, "Approvals are optional when the inbox is not reachable.");
            }
        }

        _confirmations = await TryReadConfirmationsAsync();
        _issueDrafts = await TryReadGitHubIssueDraftsAsync();

        var synced = State.SyncLiveFlows(_flows);
        await Show(synced with { Status = synced.Status });
    }

    private async Task<ConfirmationRequested[]> TryReadConfirmationsAsync()
    {
        if (ConfirmationInbox is not { } confirmationInbox)
        {
            return [];
        }

        try
        {
            var inbox = await confirmationInbox.Read();
            var now = NowUtc();
            return [.. inbox.Pending.Where(p => p.ExpiresAt > now)];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Confirmations inbox is optional in minimal TasksApp hosts.");
            return [];
        }
    }



    private async Task<DeployedSource?> UnitOfAsync(string flowId)
    {
        if (SynapseStudio is not { } studio)
        {
            return null;
        }

        var keys = new[] { AliasOf(flowId), FlowKey(flowId) }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            var units = await studio.List();
            return units.FirstOrDefault(u => keys.Any(key => u.SourceKey == key || u.Aliases.Contains(key, StringComparer.Ordinal)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "The synapse studio is optional; the alias falls back to the flow key.");
            return null;
        }
    }

    private string? AliasOf(string flowId)
    {
        var flow = _flows.FirstOrDefault(f => f.FlowId == flowId);
        if (flow?.Subject is { Length: > 0 } subject) { return subject; }
        return flow is { ConfirmationPlanId: { } plan }
            && _approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == plan) is { } consent
            ? consent.Subject
            : null;
    }

    private string AliasOrKey(string flowId) => AliasOf(flowId) ?? FlowKey(flowId);

    private async Task<string> SourceOfAsync(DeployedSource unit)
    {
        var alias = unit.Aliases.FirstOrDefault() ?? unit.SourceKey;
        var header = $"{unit.SourceKey} · {unit.Status} · build {unit.Hash}\nAliases: {string.Join(", ", unit.Aliases)}\nDeployed {unit.At:u}";
        return header + await FilesOfAsync(alias);
    }

    private async Task<string> StagedSourceAsync(string flowId)
        => AliasOf(flowId) is { } alias && await FilesOfAsync(alias) is { Length: > 0 } files
            ? $"{alias} · staged, waiting for your consent{files}"
            : "No deployed unit matches this flow.";

    private async Task<string> FilesOfAsync(string alias)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        var unavailable = false;
        if (SynapseStudio is { } studio)
        {
            try
            {
                foreach (var (path, text) in await studio.SourceFiles(alias)) { files[path] = text; }
                foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogSwallowed(error, "The unit's files are optional; the source shows what was read.");
                unavailable = true;
            }
        }
        else
        {
            unavailable = true;
        }

        var ordered = files.Where(file => file.Key == "spec.md")
            .Concat(files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            .Concat(files.Where(file => file.Key is not "spec.md" and not "scenarios.json" && !file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            .Concat(files.Where(file => file.Key == "scenarios.json"));
        var listing = string.Concat(ordered.Select(file => $"\n\n--- {file.Key} ---\n{file.Value}"));
        return unavailable ? listing + "\n\n(files unavailable)" : listing;
    }

    private DateTimeOffset NowUtc() => CurrentUtcNow(Time ?? _injectedTime);

    private Task TryFollowOptionalAsync<TNeuron, TSignal>(string key)
        where TNeuron : class
        where TSignal : Signal
        => FollowOptionalAsync<TNeuron, TSignal>(key, _logger);

    private static string FlowKey(string flowId) => flowId[(flowId.LastIndexOf('/') + 1)..];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
