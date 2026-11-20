/*
# Tasks
Role: Default
Shipped: true
Operations: open, view, filter, select, new, cancel, relate-kind, relate, show-source, ask-ino, move, advance, complete, clear-done, approve-issue-draft, dismiss-issue-draft, escalate-issue, ui.command

Workspace task planning & live brain flow orchestration (`app.tasks`) built on `AggregateScreen<TasksWorkspaceState>`
and live `IntoChat.OS.Modules.UI` neurons (`IKanbanBoard`, `IStepper`, `ITabs`, `ITable`, `ICommandBar`).
*/
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.CommandBar.Signals;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.KanbanBoard.Signals;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Stepper.Signals;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Table.Signals;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Tabs.Signals;
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

[Neuron("app.tasks")]
[Screen("app.tasks", Title = "Tasks", Icon = "tasks")]
public sealed partial class TasksApp : AggregateScreen<TasksWorkspaceState>, INavigatedTo<TasksNavArgs>, INavigatedFrom
{
    public const string AppName = "tasks";
    public const string AppOwner = "app.tasks";

    public const string KanbanKey = "tasks/kanban";
    public const string StepperKey = "tasks/stepper";
    public const string TabsKey = "tasks/tabs";
    public const string TableKey = "tasks/table";
    public const string CommandBarKey = "tasks/command-bar";

    private const string FlowsKey = "flows/index";
    private const string ApprovalsKey = "approvals/index";
    private const string SynapsesKey = "ino/synapses";

    private static readonly ILogger ConsoleLogger = OsConsoleLog.For<TasksApp>();
    private readonly ILogger _logger;
    private readonly TimeProvider? _injectedTime;

    private FlowView[] _flows = [];
    private Approval[] _approvals = [];
    private ConfirmationRequested[] _confirmations = [];
    private PendingIssueTriageDraft[] _issueDrafts = [];
    private int _lastSyncedKanbanVersion;
    private int _lastSyncedStepperVersion;
    private int _lastSyncedTabsVersion;

    public TasksApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public TasksApp(ILogger<TasksApp>? logger = null, TimeProvider? time = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
        _injectedTime = time;
    }

    public TimeProvider? Time { get; set; }

    public static AppBundle Bundle { get; } = new(
        AppName,
        "2.0.0",
        new AppManifest(
            Title: "Tasks",
            Description: "Plan and follow workspace tasks and brain flows across Kanban board, list, and stage stepper.",
            Operations:
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
                new(UiCommand.SignalAlias, "Dispatch a typed Tasks aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "tasks",
            Toolbar:
            [
                new("view", Icon: "kanban", Label: "Board"),
                new("new", Icon: "plus", Label: "Add Task"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(TasksApp).AssemblyQualifiedName,
    };

    public new TasksWorkspaceState State => base.State;

    public View Render() => PreviewView(State);

    public View RenderSide() => RenderSideCore(State, _flows, _approvals, State.Source);

    protected override Node Render(TasksWorkspaceState state)
        => RenderRootCore(state, LastRejection, _flows, _approvals, _confirmations, _issueDrafts, NowUtc(), ActiveTransition);

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

        await TryFollowOptionalAsync<IKanbanBoard, KanbanBoardChanged>(KanbanKey);
        await TryFollowOptionalAsync<IStepper, StepperChanged>(StepperKey);
        await TryFollowOptionalAsync<ITabs, TabChanged>(TabsKey);
        await TryFollowOptionalAsync<ITable, TableRowSelected>(TableKey);
        await TryFollowOptionalAsync<ICommandBar, CommandBarInvoked>(CommandBarKey);
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
        await SyncUiNeuronsAsync();
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
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task New(TasksNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Blank(request.Title) ?? Blank(State.Draft.Title) ?? "New task";
        var description = request.Description?.Trim() ?? State.Draft.Description.Trim();
        var stage = TasksWorkspaceState.TryNormalizeStage(request.Stage) ?? "backlog";
        var goal = string.IsNullOrWhiteSpace(description) ? title : description;
        var inner = JsonSerializer.Serialize(new PlanInner { Goal = goal }, JsonSerializerOptions.Web);

        if (TryGetOptional<IFlows>(FlowsKey) is { } flows)
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
                await SyncUiNeuronsAsync();
                return;
            }
        }

        await ApplyDecisionAsync(State.AddTask(title, description, stage));
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task Cancel(TasksCancel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }

        try
        {
            await Get<IFlows>(FlowsKey).Cancel(flowId, Blank(request.Reason) ?? "Cancelled from Tasks");
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
            await SyncUiNeuronsAsync();
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
            await Get<IFlows>(FlowsKey).Relate(flowId, new FlowRelation(target, kind, lag));
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
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task AskIno(TasksAskIno request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        var alias = unit?.Aliases.FirstOrDefault() ?? unit?.SourceKey ?? AliasOrKey(flowId);
        await Get<IInoFocus>(IInoFocus.DefaultKey).Point("window:ino", FocusTone.Info, $"Change unit {alias}", new FocusAction("open", "ino"));
        await Show(State.WithStatus($"Asked Ino to change {alias}"));
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task Move(TasksMove request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.TaskId) is not { } taskId || Blank(request.Stage) is not { } stage) { return; }
        await ApplyDecisionAsync(State.MoveTask(taskId, stage));
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task Advance(TasksAdvance request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.AdvanceSelectedStage());
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task Complete(TasksComplete request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.CompleteSelectedTask());
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task ClearDone(TasksClearDone request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDecisionAsync(State.ClearDoneTasks());
        await SyncUiNeuronsAsync();
    }

    [On]
    public async Task ApproveIssueDraft(TasksApproveIssueDraft request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.PreviewId) ?? Blank(State.FocusedDraftPreviewId)) is not { } previewId)
        {
            await Show(State.WithStatus("Select a pending issue triage draft to approve."));
            await SyncUiNeuronsAsync();
            return;
        }

        var matchingDraft = _issueDrafts.FirstOrDefault(d => string.Equals(d.PreviewId, previewId, StringComparison.Ordinal));
        var connKey = NormalizeGitHubConnectionKey(Blank(request.ConnectionKey) ?? matchingDraft?.ConnectionKey);
        var issueNumber = request.IssueNumber > 0 ? request.IssueNumber : (matchingDraft?.IssueNumber ?? 0);

        try
        {
            var repo = Context.Get<IRepository>(new NeuronId(Id.Brain, "github", connKey));
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
            await SyncUiNeuronsAsync();
            return;
        }

        var matchingDraft = _issueDrafts.FirstOrDefault(d => string.Equals(d.PreviewId, previewId, StringComparison.Ordinal));
        var connKey = NormalizeGitHubConnectionKey(Blank(request.ConnectionKey) ?? matchingDraft?.ConnectionKey);

        try
        {
            var repo = Context.Get<IRepository>(new NeuronId(Id.Brain, "github", connKey));
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
            await SyncUiNeuronsAsync();
            return;
        }

        var effectivePreviewId = previewId ?? string.Create(CultureInfo.InvariantCulture, $"issue-{issueNumber}");
        var issueTitle = string.Create(CultureInfo.InvariantCulture, $"Issue #{issueNumber}");
        var issueContext = matchingDraft?.Body ?? string.Empty;

        try
        {
            var repo = Context.Get<IRepository>(new NeuronId(Id.Brain, "github", connKey));
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
        if (TryGetOptional<IFlows>(FlowsKey) is { } flows)
        {
            try
            {
                escalatedFlowId = await StartEngineerFlowForIssueAsync(flows, repoLabel, taskTitle, goal);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogSwallowed(error, "The Engineer flow could not start; recording status on TasksWorkspaceState.");
                await Show(State.WithStatus($"Could not escalate issue: {error.Message}"));
                await SyncUiNeuronsAsync();
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

    // ── Screen Commands (Bound to Kit Buttons) ───────────────────────────────

    public async Task AddTaskCommand()
    {
        var title = Blank(State.Draft.Title) ?? string.Create(CultureInfo.InvariantCulture, $"Task #{State.NextTaskSequence}");
        await ApplyDecisionAsync(State.AddTask(title, State.Draft.Description, "backlog"));
        await SyncUiNeuronsAsync();
    }

    public async Task AdvanceStageCommand()
    {
        await ApplyDecisionAsync(State.AdvanceSelectedStage());
        await SyncUiNeuronsAsync();
    }

    public async Task CompleteTaskCommand()
    {
        await ApplyDecisionAsync(State.CompleteSelectedTask());
        await SyncUiNeuronsAsync();
    }

    public async Task ClearDoneCommand()
    {
        await ApplyDecisionAsync(State.ClearDoneTasks());
        await SyncUiNeuronsAsync();
    }

    public Task SelectFlowCommand(string flowId) => Select(new TasksSelect(flowId));

    public Task ShowSourceCommand(string flowId) => ShowSource(new TasksShowSource(flowId));

    public Task AskInoCommand(string flowId) => AskIno(new TasksAskIno(flowId));

    public Task ApproveIssueDraftCommand(string connectionKey, int issueNumber, string previewId)
        => ApproveIssueDraft(new TasksApproveIssueDraft(connectionKey, issueNumber, previewId));

    public Task DismissIssueDraftCommand(string connectionKey, string previewId)
        => DismissIssueDraft(new TasksDismissIssueDraft(connectionKey, previewId));

    public Task EscalateIssueCommand(string connectionKey, string repository, int issueNumber, string previewId)
        => EscalateIssue(new TasksEscalateIssue(connectionKey, repository, issueNumber, previewId));

    public Task ClearFilterCommand() => Filter(new TasksFilter());

    public Task ToggleStatusFilterCommand(string status)
    {
        var current = State.Query.Status?.ToString();
        var nextStatus = string.Equals(current, status, StringComparison.OrdinalIgnoreCase) ? null : status;
        return Filter(new TasksFilter(Owner: State.Query.Owner, Status: nextStatus, Type: State.Query.Type));
    }

    public Task ToggleOwnerFilterCommand(string owner)
    {
        var nextOwner = string.Equals(State.Query.Owner, owner, StringComparison.Ordinal) ? null : owner;
        return Filter(new TasksFilter(Owner: nextOwner, Status: State.Query.Status?.ToString(), Type: State.Query.Type));
    }

    public async Task OnField(string field, FieldChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var value = (await Get<IField>(FieldKey(field)).Read()).Value ?? "";
        await Show(State.UpdateDraftField(field, value));
    }

    // ── Modules.UI & Flows Synapse Reactions (Version-Guarded Anti-Clobber) ──

    [On]
    public async Task OnKanbanChanged(KanbanBoardChanged? changed)
    {
        if (changed is null || string.IsNullOrWhiteSpace(changed.BoardId))
        {
            return;
        }

        if (!MatchesNeuronKey(changed.BoardId, KanbanKey) || changed.Version <= _lastSyncedKanbanVersion)
        {
            return;
        }

        _lastSyncedKanbanVersion = Math.Max(_lastSyncedKanbanVersion, changed.Version);

        if (!string.IsNullOrWhiteSpace(changed.MovedCardId) && !string.IsNullOrWhiteSpace(changed.TargetColumnId))
        {
            var movedId = changed.MovedCardId.Trim();
            var targetStage = TasksWorkspaceState.TryNormalizeStage(changed.TargetColumnId);
            if (targetStage is not null)
            {
                await ApplyDecisionAsync(State.MoveTask(movedId, targetStage));
                await SyncUiNeuronsAsync(skipKanbanConfigure: true);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(changed.SelectedCardId)
            && !string.Equals(changed.SelectedCardId, State.SelectedTaskId, StringComparison.Ordinal))
        {
            var selectedId = changed.SelectedCardId.Trim();
            if (!State.Tasks.Any(t => string.Equals(t.Id, selectedId, StringComparison.Ordinal))
                && TryGetOptional<IKanbanBoard>(KanbanKey) is { } kanban)
            {
                var boardState = await kanban.Read();
                if (boardState.Cards.FirstOrDefault(c => string.Equals(c.Id, selectedId, StringComparison.Ordinal)) is { } newCard)
                {
                    await ApplyDecisionAsync(State.AddTask(newCard.Title, newCard.Subtitle, newCard.ColumnId));
                }
            }

            await Show(State.SelectTaskOrFlow(selectedId));
            await SyncUiNeuronsAsync(skipKanbanConfigure: true);
        }
    }

    [On]
    public async Task OnStepperChanged(StepperChanged? changed)
    {
        if (changed is null || string.IsNullOrWhiteSpace(changed.StepperId) || string.IsNullOrWhiteSpace(changed.ActiveStepId))
        {
            return;
        }

        if (!MatchesNeuronKey(changed.StepperId, StepperKey) || changed.Version <= _lastSyncedStepperVersion)
        {
            return;
        }

        _lastSyncedStepperVersion = Math.Max(_lastSyncedStepperVersion, changed.Version);

        var targetStage = TasksWorkspaceState.TryNormalizeStage(changed.ActiveStepId);
        if (targetStage is null || State.SelectedTask is not { } selected)
        {
            return;
        }

        if (string.Equals(selected.Stage, targetStage, StringComparison.Ordinal))
        {
            return;
        }

        await ApplyDecisionAsync(State.MoveTask(selected.Id, targetStage));
        await SyncUiNeuronsAsync(skipStepperConfigure: true);
    }

    [On]
    public async Task OnTabChanged(TabChanged? changed)
    {
        if (changed is null || string.IsNullOrWhiteSpace(changed.TabsId) || string.IsNullOrWhiteSpace(changed.SelectedId))
        {
            return;
        }

        if (!MatchesNeuronKey(changed.TabsId, TabsKey) || changed.Version <= _lastSyncedTabsVersion)
        {
            return;
        }

        _lastSyncedTabsVersion = Math.Max(_lastSyncedTabsVersion, changed.Version);
        var normalized = TasksWorkspaceState.NormalizeTab(changed.SelectedId);
        if (string.Equals(State.ActiveTab, normalized, StringComparison.Ordinal))
        {
            return;
        }

        await Show(State.SelectTab(normalized));
        await SyncUiNeuronsAsync(skipTabsConfigure: true);
    }

    [On]
    public async Task OnTableRowSelected(TableRowSelected? selected)
    {
        if (selected is null || string.IsNullOrWhiteSpace(selected.TableId) || selected.RowIndex is not { } rowIndex || rowIndex < 0)
        {
            return;
        }

        if (!MatchesNeuronKey(selected.TableId, TableKey))
        {
            return;
        }

        var visible = State.FilteredTasks(_flows);
        if (rowIndex < visible.Length)
        {
            var chosen = visible[rowIndex];
            if (!string.Equals(State.SelectedTaskId, chosen.Id, StringComparison.Ordinal))
            {
                await Show(State.SelectTaskOrFlow(chosen.Id));
                await SyncUiNeuronsAsync();
            }
        }
    }

    [On]
    public Task OnCommandBarInvoked(CommandBarInvoked? invoked)
    {
        if (invoked is null || string.IsNullOrWhiteSpace(invoked.BarId) || string.IsNullOrWhiteSpace(invoked.ItemId))
        {
            return Task.CompletedTask;
        }

        if (!MatchesNeuronKey(invoked.BarId, CommandBarKey))
        {
            return Task.CompletedTask;
        }

        return invoked.ItemId switch
        {
            "add-task" => AddTaskCommand(),
            "advance-stage" => AdvanceStageCommand(),
            "complete-task" => CompleteTaskCommand(),
            "clear-done" => ClearDoneCommand(),
            _ => Task.CompletedTask,
        };
    }

    [On]
    public Task OnFlowsChanged(FlowsChanged? changed)
        => changed is null ? Task.CompletedTask : RefreshFlowsAndSyncAsync();

    // ── Live Synchronization with Modules.UI & Brain Flows ───────────────────

    private async Task RefreshFlowsAndSyncAsync()
    {
        try
        {
            _flows = await Get<IFlows>(FlowsKey).List(State.Query);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "The flows index is optional; the board shows workspace tasks and last read flows.");
        }

        try
        {
            _approvals = [.. await Get<IApprovals>(ApprovalsKey).Pending()];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, "Approvals are optional when the inbox is not reachable.");
        }

        _confirmations = await TryReadConfirmationsAsync();
        _issueDrafts = await TryReadGitHubIssueDraftsAsync();

        var synced = State.SyncLiveFlows(_flows);
        await Show(synced with { Status = synced.Status });
        await SyncUiNeuronsAsync();
    }

    private async Task<ConfirmationRequested[]> TryReadConfirmationsAsync()
    {
        try
        {
            var inboxId = new NeuronId(Id.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey);
            var inbox = await Context.Get<IConfirmationInbox>(inboxId).Read();
            var now = NowUtc();
            return [.. inbox.Pending.Where(p => p.ExpiresAt > now)];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Confirmations inbox is optional in minimal TasksApp hosts.");
            return [];
        }
    }

    private async Task<PendingIssueTriageDraft[]> TryReadGitHubIssueDraftsAsync()
    {
        List<string> connectionKeys = [];
        BrainId brain;
        try
        {
            brain = Id.Brain;
        }
        catch (InvalidOperationException error)
        {
            _logger.LogDebug(error, "TasksApp brain id is unavailable outside neuron activation.");
            return [];
        }

        try
        {
            var catalog = await Context.Get<IConnectionCatalog>(new NeuronId(brain, IConnectionCatalog.TypeName, "main")).Read();
            foreach (var row in catalog.Rows.Where(static r => string.Equals(r.ConnectorId, "github", StringComparison.Ordinal)))
            {
                if (!string.IsNullOrWhiteSpace(row.ConnectionId))
                {
                    connectionKeys.Add(string.Concat("connection/github/", Uri.EscapeDataString(row.ConnectionId)));
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Connection catalog is optional when probing GitHub triage drafts.");
        }

        if (!connectionKeys.Contains("connection/github/main", StringComparer.Ordinal))
        {
            connectionKeys.Add("connection/github/main");
        }

        var now = NowUtc();
        var results = new List<PendingIssueTriageDraft>();
        var seenPreviews = new HashSet<string>(StringComparer.Ordinal);

        foreach (var connKey in connectionKeys.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var repo = Context.Get<IRepository>(new NeuronId(brain, "github", connKey));
                var drafts = await repo.ReadPendingIssueDrafts();
                foreach (var draft in drafts)
                {
                    if (draft.ExpiresAt <= now || !seenPreviews.Add(draft.PreviewId))
                    {
                        continue;
                    }

                    var repoLabel = !string.IsNullOrWhiteSpace(draft.Repository) ? draft.Repository : connKey;
                    results.Add(new PendingIssueTriageDraft(
                        connKey,
                        repoLabel,
                        draft.PreviewId,
                        draft.IssueNumber,
                        draft.Body,
                        draft.SuggestedLabels ?? [],
                        draft.ExpiresAt));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "GitHub connection {ConnectionKey} has no pending issue triage drafts or is unhosted.", connKey);
            }
        }

        return [.. results];
    }

    private async Task SyncUiNeuronsAsync(
        bool skipKanbanConfigure = false,
        bool skipStepperConfigure = false,
        bool skipTabsConfigure = false)
    {
        try
        {
            var state = State;
            var visible = state.FilteredTasks(_flows);

            if (TryGetOptional<ICommandBar>(CommandBarKey) is { } cmdBar)
            {
                await cmdBar.Configure(
                [
                    new CommandBarItem("add-task", "Add Task", "plus", Disabled: state.BoardHasCapacity().Broken),
                    new CommandBarItem("advance-stage", "Advance Stage", "arrow-right", Disabled: state.CanAdvanceSelectedTask().Broken),
                    new CommandBarItem("complete-task", "Complete", "circle-check", Disabled: state.CanCompleteSelectedTask().Broken),
                    new CommandBarItem("clear-done", "Clear Done", "trash", Primary: false, Disabled: state.HasCompletedTasks().Broken),
                ]);
            }

            if (!skipTabsConfigure && TryGetOptional<ITabs>(TabsKey) is { } tabs)
            {
                var currentTabs = await tabs.Read();
                var targetUiTab = TasksWorkspaceState.UiTabId(state.ActiveTab);
                if (currentTabs.Version == 0 || !string.Equals(currentTabs.SelectedId, targetUiTab, StringComparison.Ordinal))
                {
                    var tabsState = await tabs.Configure(
                    [
                        new UiTabItem("board", "Board"),
                        new UiTabItem("table", "Table"),
                        new UiTabItem("approvals", "Approvals"),
                    ], targetUiTab);
                    _lastSyncedTabsVersion = Math.Max(_lastSyncedTabsVersion, tabsState.Version);
                }
            }

            if (!skipStepperConfigure && TryGetOptional<IStepper>(StepperKey) is { } stepper)
            {
                var currentStepper = await stepper.Read();
                var steps = BuildStepperSteps(state);
                var activeStage = state.SelectedTask?.Stage ?? "backlog";
                var stepperTitle = state.SelectedTask is { } task
                    ? $"Lifecycle · {task.Title}"
                    : "Task Lifecycle";

                if (currentStepper.Version == 0
                    || !string.Equals(currentStepper.ActiveStepId, activeStage, StringComparison.Ordinal)
                    || !string.Equals(currentStepper.Title, stepperTitle, StringComparison.Ordinal)
                    || !StepperStepsMatch(currentStepper.Steps, steps))
                {
                    var stepperState = await stepper.Configure(steps, activeStage, stepperTitle);
                    _lastSyncedStepperVersion = Math.Max(_lastSyncedStepperVersion, stepperState.Version);
                }
            }

            if (!skipKanbanConfigure && TryGetOptional<IKanbanBoard>(KanbanKey) is { } kanban)
            {
                var currentKanban = await kanban.Read();
                KanbanColumn[] cols =
                [
                    new("backlog", "Backlog", "list-todo"),
                    new("in-progress", "In Progress", "play"),
                    new("review", "Review", "eye"),
                    new("done", "Done", "circle-check"),
                ];
                var cards = BuildKanbanCards(visible);
                var selectedCardId = state.SelectedTaskId is { } sel && cards.Any(c => string.Equals(c.Id, sel, StringComparison.Ordinal))
                    ? sel
                    : cards.FirstOrDefault()?.Id;

                if (currentKanban.Version == 0 || !KanbanMatches(currentKanban, cards, selectedCardId))
                {
                    var kanbanState = await kanban.Configure("Tasks Board", cols, cards, selectedCardId);
                    _lastSyncedKanbanVersion = Math.Max(_lastSyncedKanbanVersion, kanbanState.Version);
                }
            }

            if (TryGetOptional<ITable>(TableKey) is { } table)
            {
                await table.Render(
                    "Workspace Tasks & Brain Flows",
                    [
                        new TableColumn("id", "ID"),
                        new TableColumn("title", "Title"),
                        new TableColumn("stage", "Stage"),
                        new TableColumn("owner", "Owner"),
                        new TableColumn("tag", "Kind"),
                    ],
                    visible.Select(static t => (IReadOnlyList<string>)
                    [
                        t.Id,
                        t.Title,
                        TasksWorkspaceState.StageTitle(t.Stage),
                        t.Owner,
                        t.Tag,
                    ]).ToArray());
            }
        }
#pragma warning disable CA1031
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogDebug(error, "Tasks UI neuron sync skipped in minimal test host.");
        }
    }

    private static StepperStep[] BuildStepperSteps(TasksWorkspaceState state)
    {
        var activeStage = state.SelectedTask?.Stage ?? "backlog";
        var activeIdx = Array.IndexOf(TasksWorkspaceState.StageIds, activeStage);
        if (activeIdx < 0) { activeIdx = 0; }

        return TasksWorkspaceState.StageIds.Select((stageId, idx) =>
        {
            var status = activeStage == "done"
                ? StepperStatuses.Done
                : idx < activeIdx
                    ? StepperStatuses.Done
                    : idx == activeIdx
                        ? StepperStatuses.Active
                        : StepperStatuses.Pending;
            return new StepperStep(stageId, TasksWorkspaceState.StageTitle(stageId), status);
        }).ToArray();
    }

    private static KanbanCard[] BuildKanbanCards(TaskWorkItem[] visible)
    {
        var list = new List<KanbanCard>(visible.Length);
        foreach (var stage in TasksWorkspaceState.StageIds)
        {
            var inStage = visible.Where(t => string.Equals(t.Stage, stage, StringComparison.Ordinal)).ToArray();
            for (var i = 0; i < inStage.Length; i++)
            {
                var item = inStage[i];
                var subtitle = string.IsNullOrWhiteSpace(item.Description) ? $"Owner: {item.Owner}" : item.Description;
                list.Add(new KanbanCard(
                    Id: item.Id,
                    ColumnId: stage,
                    Title: item.Title,
                    Subtitle: subtitle,
                    Tag: item.Tag,
                    Assignee: item.Owner,
                    Order: i));
            }
        }

        return [.. list];
    }

    private static bool StepperStepsMatch(StepperStep[] actual, StepperStep[] expected)
    {
        if (actual.Length != expected.Length) { return false; }
        for (var i = 0; i < actual.Length; i++)
        {
            if (!string.Equals(actual[i].Id, expected[i].Id, StringComparison.Ordinal)
                || !string.Equals(actual[i].Status, expected[i].Status, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool KanbanMatches(KanbanBoardState current, KanbanCard[] expectedCards, string? expectedSelectedId)
    {
        if (!string.Equals(current.SelectedCardId, expectedSelectedId, StringComparison.Ordinal)
            || current.Cards.Length != expectedCards.Length)
        {
            return false;
        }

        for (var i = 0; i < expectedCards.Length; i++)
        {
            var a = current.Cards[i];
            var b = expectedCards[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || !string.Equals(a.ColumnId, b.ColumnId, StringComparison.Ordinal)
                || !string.Equals(a.Title, b.Title, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ── Declarative View Composition (AppShellNode + Modules.UI) ─────────────

    public static string FieldKey(string field) => $"apps/{AppName}/{field}";

    public static Intent OpenTrace(string traceId) => AppSignals.OpenApp("creator", $"trace/{traceId}");

    public static Intent BuildEngineerIssueIntent(string repository, int issueNumber, string? escalatedFlowId = null)
    {
        var repo = string.IsNullOrWhiteSpace(repository) ? "os" : repository.Trim();
        var targetId = issueNumber > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{repo}#{issueNumber}")
            : repo;
        return AppSignals.OpenApp(
            "engineer",
            new StudioNavArgs(
                TargetId: targetId,
                Lens: "plan",
                FlowId: Blank(escalatedFlowId)));
    }

    private static Node RenderRootCore(
        TasksWorkspaceState state,
        Rejected? lastRejection,
        FlowView[] liveFlows,
        Approval[] approvals,
        ConfirmationRequested[] confirmations,
        PendingIssueTriageDraft[] issueDrafts,
        DateTimeOffset now,
        string transition = "")
    {
        var visible = state.FilteredTasks(liveFlows);
        var activeTab = TasksWorkspaceState.NormalizeTab(state.ActiveTab);

        var toolbar = RenderTopBar(state, approvals, issueDrafts);
        var sidebar = RenderSidebar(state, visible, liveFlows, approvals);
        var inspector = RenderRightInspector(state, liveFlows, approvals, confirmations, issueDrafts);

        var body = activeTab switch
        {
            "table" or "list" => RenderFullBleedStage(UiNeuron("ui.table", TableKey), lastRejection),
            "approvals" or "details" => RenderApprovalsAndDetailsTab(state, lastRejection, liveFlows, approvals, confirmations, issueDrafts, now),
            _ => RenderFullBleedStage(UiNeuron("ui.kanban", KanbanKey), lastRejection),
        };

        return BindOwner(
            AppShell(
                key: "tasks-shell",
                sidebar: sidebar,
                body: body,
                toolbar: toolbar,
                inspector: inspector,
                automationName: "Tasks Studio",
                transition: transition),
            AppOwner);
    }

    private static Node RenderTopBar(
        TasksWorkspaceState state,
        Approval[] approvals,
        PendingIssueTriageDraft[] issueDrafts)
    {
        var isAllFilter = state.Query.Status is null && string.IsNullOrWhiteSpace(state.Query.Owner);
        var isRunningFilter = state.Query.Status == FlowStatus.Running;
        var isWaitingFilter = state.Query.Status == FlowStatus.Waiting;
        var isInoOwner = string.Equals(state.Query.Owner, "ino", StringComparison.Ordinal);
        var pendingGates = approvals.Length + issueDrafts.Length;

        List<Node?> items =
        [
            UiNeuron("ui.tabs", TabsKey),
            (isAllFilter ? PrimaryButton("All") : GhostButton("All"))
                .Key("tasks:filter:all")
                .Small()
                .OnClickAsync<TasksApp>(app => app.ClearFilterCommand()),
            (isRunningFilter ? PrimaryButton("Active") : GhostButton("Active"))
                .Key("tasks:filter:running")
                .Small()
                .OnClickAsync<TasksApp>(app => app.ToggleStatusFilterCommand("Running")),
            (isWaitingFilter ? PrimaryButton("Review") : GhostButton("Review"))
                .Key("tasks:filter:waiting")
                .Small()
                .OnClickAsync<TasksApp>(app => app.ToggleStatusFilterCommand("Waiting")),
            (isInoOwner ? PrimaryButton("Owner: ino") : GhostButton("Owner: ino"))
                .Key("tasks:filter:ino")
                .Small()
                .OnClickAsync<TasksApp>(app => app.ToggleOwnerFilterCommand("ino")),
            pendingGates > 0
                ? StatusPill(StatusKind.NeedsYou, string.Create(CultureInfo.InvariantCulture, $"{pendingGates} Gate(s)"))
                : null,
            FlexSpacer(),
            PrimaryButton("New Task", Icons.Plus)
                .Key("tasks:add")
                .Small()
                .Guard(state.BoardHasCapacity())
                .OnClickAsync<TasksApp>(app => app.AddTaskCommand()),
            new Button("tasks:open-engineer", "Engineer ↗", Intent: AppSignals.OpenApp("engineer", state.SelectedFlowId))
            {
                Small = true,
                Icon = Icons.Sparkles,
                AutomationName = "Open Engineer",
            },
            new Button("tasks:open-creator", "Creator ↗", Intent: AppSignals.OpenApp("creator", state.SelectedFlowId))
            {
                Small = true,
                Icon = "git-branch",
                AutomationName = "Open Creator",
            },
        ];

        return HStack(Space.Xs, [.. items]);
    }

    private static Node RenderFullBleedStage(Node stageNeuron, Rejected? lastRejection)
    {
        if (lastRejection is not null)
        {
            return new Column(
            [
                Banner("Domain rule refused command", string.Join(" ", lastRejection.Messages), tone: "error"),
                stageNeuron,
            ])
            {
                Gap = Space.Xs,
                Extents = [0, 1.0],
            };
        }

        return new Column([stageNeuron])
        {
            Gap = 0,
            Extents = [1.0],
        };
    }

    private static Node RenderSidebar(
        TasksWorkspaceState state,
        TaskWorkItem[] visible,
        FlowView[] liveFlows,
        Approval[] approvals)
    {
        var startEmit = AppSignals.Emit(AppName, "new", state.Draft.Title, state.Draft.Description);
        var statusLine = approvals.Length == 0
            ? state.Status
            : string.Create(CultureInfo.InvariantCulture, $"{state.Status} · {approvals.Length} pending approval(s)");

        List<Node?> children =
        [
            SectionHeader("Quick Task", string.Create(CultureInfo.InvariantCulture, $"{visible.Length} visible")),
            TextField(FieldKey("title"), "Title").Key("title"),
            TextField(FieldKey("description"), "Description").Key("description").Emits(startEmit),
            PrimaryButton("Start", Icons.Plus).Key("start").Small().Emits(startEmit),
        ];

        if (liveFlows.Length > 0)
        {
            children.Add(SectionHeader(
                "Live Flows",
                approvals.Length > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{liveFlows.Length} flow(s) · {approvals.Length} approval(s)")
                    : string.Create(CultureInfo.InvariantCulture, $"{liveFlows.Length} active")));
            foreach (var flow in liveFlows)
            {
                children.Add(Button(flow.Title, "activity")
                    .Key($"open:{flow.FlowId}")
                    .Small()
                    .OnClickAsync<TasksApp>(app => app.SelectFlowCommand(flow.FlowId))
                    .ToNode());
            }
        }

        children.Add(MutedText(statusLine));
        return VStack(Space.Sm, [.. children]);
    }

    private static Node RenderRightInspector(
        TasksWorkspaceState state,
        FlowView[] liveFlows,
        Approval[] approvals,
        ConfirmationRequested[] confirmations,
        PendingIssueTriageDraft[] issueDrafts)
    {
        var selectedTask = state.SelectedTask;
        var targetFlowId = state.SelectedFlowId;
        var matchingFlow = targetFlowId is { Length: > 0 } fid
            ? liveFlows.FirstOrDefault(f => string.Equals(f.FlowId, fid, StringComparison.Ordinal))
            : null;

        List<Node?> children =
        [
            SectionHeader(
                "Inspector",
                selectedTask?.Title ?? matchingFlow?.Title ?? "Select a task or flow"),
            UiNeuron("ui.stepper", StepperKey),
            HStack(Space.Xs)[
                Button("Advance", "arrow-right")
                    .Key("tasks:advance")
                    .Small()
                    .Guard(state.CanAdvanceSelectedTask())
                    .OnClickAsync<TasksApp>(app => app.AdvanceStageCommand()),
                Button("Complete", "circle-check")
                    .Key("tasks:complete")
                    .Small()
                    .Guard(state.CanCompleteSelectedTask())
                    .OnClickAsync<TasksApp>(app => app.CompleteTaskCommand()),
                GhostButton("Clear Done", "trash")
                    .Key("tasks:clear-done")
                    .Small()
                    .Guard(state.HasCompletedTasks())
                    .OnClickAsync<TasksApp>(app => app.ClearDoneCommand())
            ],
        ];

        if (selectedTask is not null)
        {
            children.Add(HStack(Space.Xs)[
                StatusPill(StageStatusKind(selectedTask.Stage), TasksWorkspaceState.StageTitle(selectedTask.Stage)),
                StatusPill(StatusKind.Idle, $"Owner: {selectedTask.Owner}"),
                StatusPill(StatusKind.Idle, selectedTask.Tag)
            ]);
            if (!string.IsNullOrWhiteSpace(selectedTask.Description))
            {
                children.Add(MutedText(selectedTask.Description));
            }
        }

        if (matchingFlow is not null)
        {
            children.Add(SectionHeader("Flow Stages", matchingFlow.Status.ToString()));
            children.Add(new JobCardNode(
                matchingFlow.Title,
                [.. matchingFlow.Steps.Select(step => StepNode(matchingFlow, step))],
                matchingFlow.Fraction,
                matchingFlow.Status.ToString().ToLowerInvariant()));

            List<Node?> flowActions =
            [
                new Button("inspector:open-trace", "Open trace", Intent: OpenTrace(matchingFlow.TraceId))
                {
                    Small = true,
                    Icon = "activity",
                    AutomationName = "Open trace",
                },
            ];

            if (matchingFlow.Type == BuildFlow.Alias)
            {
                flowActions.Add(Button("Source", "code")
                    .Key("inspector:show-source")
                    .Small()
                    .OnClickAsync<TasksApp>(app => app.ShowSourceCommand(matchingFlow.FlowId)));
                flowActions.Add(Button("Ask Ino", Icons.Sparkles)
                    .Key("inspector:ask-ino")
                    .Small()
                    .OnClickAsync<TasksApp>(app => app.AskInoCommand(matchingFlow.FlowId)));
            }

            children.Add(HStack(Space.Xs, [.. flowActions]));
        }

        if (state.Source is { Length: > 0 } src)
        {
            children.Add(CodeBlock("Unit Source", src, maxHeight: 220));
        }

        // SHA-Bound & Person-Gated Confirmations (IConfirmation)
        children.Add(SectionHeader(
            "Confirmations (Person Gate)",
            "Human Person confirmation required for SHA-bound & destructive gates"));
        if (confirmations.Length > 0 || approvals.Length > 0)
        {
            foreach (var conf in confirmations)
            {
                var sha = ExtractBoundSha(conf.PlanId, conf.Plan.TargetLabel, conf.Plan.Body);
                var shaBadge = sha is not null ? $"SHA {sha[..Math.Min(8, sha.Length)]}" : "Person Gate";
                var isFocused = string.Equals(state.FocusedConfirmationId, conf.PlanId, StringComparison.Ordinal);
                children.Add(HStack(Space.Xs)[
                    StatusPill(isFocused ? StatusKind.Working : StatusKind.NeedsYou, shaBadge),
                    MutedText(conf.Plan.RequiresPerson ? "Person-only rule enforced" : $"Plan: {conf.PlanId}")
                ]);
                var visual = VisualCatalog.ForApproval(conf.Plan.TargetLabel);
                children.Add(new ApprovalCardNode(
                    $"inspector:confirm:{conf.PlanId}",
                    conf.PlanId,
                    conf.Plan.Title,
                    conf.Plan.Body,
                    sha is not null ? [new ApprovalRowNode("Bound SHA", "HEAD", sha)] : [],
                    Icon: visual.IconKey,
                    CapabilityLabel: visual.Label));
            }

            var coveredPlans = confirmations.Select(static c => c.PlanId).ToHashSet(StringComparer.Ordinal);
            foreach (var apprv in approvals.Where(a => !coveredPlans.Contains(a.PlanId)))
            {
                var sha = ExtractBoundSha(apprv.PlanId, apprv.Subject, apprv.Detail);
                var visual = VisualCatalog.ForApproval(apprv.Subject);
                children.Add(new ApprovalCardNode(
                    $"inspector:approval:{apprv.PlanId}",
                    apprv.PlanId,
                    apprv.Title,
                    apprv.Detail,
                    sha is not null ? [new ApprovalRowNode("Bound SHA", "HEAD", sha)] : [],
                    Icon: visual.IconKey,
                    CapabilityLabel: visual.Label));
            }
        }
        else
        {
            children.Add(MutedText("No pending SHA-bound confirmations. Only a signed-in human Person may resolve confirmation gates."));
        }

        // Pending GitHub Issue Triage Comment Drafts ({brain}/connection/github/*)
        var activeTab = TasksWorkspaceState.NormalizeTab(state.ActiveTab);
        var draftKeyPrefix = activeTab is "approvals" or "details" ? "inspector:" : string.Empty;
        var orphanEscalations = state.EscalatedIssues
            .Where(link => !issueDrafts.Any(d => string.Equals(d.PreviewId, link.PreviewId, StringComparison.Ordinal)))
            .ToArray();

        children.Add(SectionHeader(
            "GitHub Issue Triage Drafts",
            issueDrafts.Length > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{issueDrafts.Length} DraftForApproval preview(s)")
                : "Synced from connection/github/*"));
        if (issueDrafts.Length > 0)
        {
            foreach (var draft in issueDrafts)
            {
                children.Add(RenderIssueTriageDraftItem(state, draft, draftKeyPrefix, draft.PreviewId));
            }
        }
        else
        {
            children.Add(MutedText("No pending GitHub issue triage comment drafts."));
        }

        foreach (var escalated in orphanEscalations)
        {
            children.Add(RenderEscalatedIssueLinkItem(escalated, draftKeyPrefix));
        }

        return VStack(Space.Sm, [.. children]);
    }

    private static Node RenderIssueTriageDraftItem(
        TasksWorkspaceState state,
        PendingIssueTriageDraft draft,
        string keyPrefix,
        string cardId)
    {
        var labelsNote = draft.SuggestedLabels.Length > 0
            ? $"Labels: {string.Join(", ", draft.SuggestedLabels)}"
            : "Triage comment preview";
        var isFocused = string.Equals(state.FocusedDraftPreviewId, draft.PreviewId, StringComparison.Ordinal);
        var escalatedFlowId = state.FindEscalatedFlowId(draft.PreviewId, draft.Repository, draft.IssueNumber);
        var studioIntent = BuildEngineerIssueIntent(draft.Repository, draft.IssueNumber, escalatedFlowId);

        return VStack(Space.Xs)[
            DraftCard(
                id: cardId,
                channel: "GitHub Issue",
                to: string.Create(CultureInfo.InvariantCulture, $"{draft.Repository} #{draft.IssueNumber}"),
                body: draft.Body,
                from: draft.ConnectionKey,
                subject: labelsNote,
                status: isFocused ? "focused" : "pending"),
            HStack(Space.Xs)[
                PrimaryButton("Approve & Post", "circle-check")
                    .Key($"{keyPrefix}tasks:approve-issue-draft:{draft.PreviewId}")
                    .Small()
                    .OnClickAsync<TasksApp>(app => app.ApproveIssueDraftCommand(draft.ConnectionKey, draft.IssueNumber, draft.PreviewId)),
                GhostButton("Dismiss", "x")
                    .Key($"{keyPrefix}tasks:dismiss-issue-draft:{draft.PreviewId}")
                    .Small()
                    .OnClickAsync<TasksApp>(app => app.DismissIssueDraftCommand(draft.ConnectionKey, draft.PreviewId)),
                Button("Fix in Studio ↗", Icons.Sparkles)
                    .Key($"{keyPrefix}tasks:escalate-issue:{draft.PreviewId}")
                    .Small()
                    .OnClickAsync<TasksApp>(app => app.EscalateIssueCommand(draft.ConnectionKey, draft.Repository, draft.IssueNumber, draft.PreviewId))
            ],
            HStack(Space.Xs)[
                escalatedFlowId is not null
                    ? StatusPill(StatusKind.Working, string.Create(CultureInfo.InvariantCulture, $"Run: {FlowKey(escalatedFlowId)}"))
                    : null,
                new Button(
                    $"{keyPrefix}tasks:open-engineer-issue:{draft.PreviewId}",
                    escalatedFlowId is not null ? "Open Run in Studio ↗" : "Open Engineer Studio ↗",
                    Intent: studioIntent)
                {
                    Small = true,
                    Style = "ghost",
                    Icon = "external-link",
                    AutomationName = "Open in Engineer Studio",
                }
            ]
        ];
    }

    private static Node RenderEscalatedIssueLinkItem(EscalatedIssueLink link, string keyPrefix)
        => HStack(Space.Xs)[
            StatusPill(StatusKind.Working, string.Create(CultureInfo.InvariantCulture, $"{link.Repository} #{link.IssueNumber}")),
            new Button(
                $"{keyPrefix}tasks:open-engineer-issue:{link.PreviewId}",
                "Open Run in Studio ↗",
                Intent: BuildEngineerIssueIntent(link.Repository, link.IssueNumber, link.FlowId))
            {
                Small = true,
                Style = "ghost",
                Icon = "external-link",
                AutomationName = "Open in Engineer Studio",
            }
        ];

    private static Node RenderApprovalsAndDetailsTab(
        TasksWorkspaceState state,
        Rejected? lastRejection,
        FlowView[] liveFlows,
        Approval[] approvals,
        ConfirmationRequested[] confirmations,
        PendingIssueTriageDraft[] issueDrafts,
        DateTimeOffset now)
    {
        List<Node?> sections = [];

        if (lastRejection is not null)
        {
            sections.Add(Banner("Domain rule refused command", string.Join(" ", lastRejection.Messages), tone: "error"));
        }

        sections.Add(Banner(
            "Human Person Confirmation Rules",
            "SHA-bound PR approvals, synapse capability installs, and DraftForApproval GitHub issue comments require an authenticated human Person.",
            tone: "neutral"));

        if (confirmations.Length > 0 || approvals.Length > 0)
        {
            List<Node?> gateCards = [];
            var coveredPlans = new HashSet<string>(StringComparer.Ordinal);
            foreach (var conf in confirmations)
            {
                coveredPlans.Add(conf.PlanId);
                var sha = ExtractBoundSha(conf.PlanId, conf.Plan.TargetLabel, conf.Plan.Body);
                var visual = VisualCatalog.ForApproval(conf.Plan.TargetLabel);
                gateCards.Add(new ApprovalCardNode(
                    $"tab:confirm:{conf.PlanId}",
                    conf.PlanId,
                    conf.Plan.Title,
                    conf.Plan.Body,
                    sha is not null ? [new ApprovalRowNode("Bound SHA", "HEAD", sha)] : [],
                    Icon: visual.IconKey,
                    CapabilityLabel: visual.Label));
            }

            foreach (var apprv in approvals.Where(a => !coveredPlans.Contains(a.PlanId)))
            {
                var sha = ExtractBoundSha(apprv.PlanId, apprv.Subject, apprv.Detail);
                var visual = VisualCatalog.ForApproval(apprv.Subject);
                gateCards.Add(new ApprovalCardNode(
                    $"tab:approval:{apprv.PlanId}",
                    apprv.PlanId,
                    apprv.Title,
                    apprv.Detail,
                    sha is not null ? [new ApprovalRowNode("Bound SHA", "HEAD", sha)] : [],
                    Icon: visual.IconKey,
                    CapabilityLabel: visual.Label));
            }

            sections.Add(Surface(SurfaceKind.Elevated, "Pending Confirmations", string.Create(CultureInfo.InvariantCulture, $"{gateCards.Count} item(s)"))[
                VStack(Space.Sm, [.. gateCards])
            ]);
        }

        var orphanTabEscalations = state.EscalatedIssues
            .Where(link => !issueDrafts.Any(d => string.Equals(d.PreviewId, link.PreviewId, StringComparison.Ordinal)))
            .ToArray();
        if (issueDrafts.Length > 0 || orphanTabEscalations.Length > 0)
        {
            List<Node> draftNodes =
            [
                .. issueDrafts.Select(draft => RenderIssueTriageDraftItem(state, draft, string.Empty, $"tab:{draft.PreviewId}")),
                .. orphanTabEscalations.Select(link => RenderEscalatedIssueLinkItem(link, string.Empty)),
            ];
            sections.Add(Surface(
                SurfaceKind.Elevated,
                "Pending GitHub Issue Triage Drafts",
                string.Create(CultureInfo.InvariantCulture, $"{issueDrafts.Length} draft(s)"))[
                VStack(Space.Sm, [.. draftNodes])
            ]);
        }

        if (state.SelectedTask is { } selected)
        {
            var matchingFlow = liveFlows.FirstOrDefault(f => string.Equals(f.FlowId, selected.FlowId ?? selected.Id, StringComparison.Ordinal));
            List<Node?> detailNodes =
            [
                HStack(Space.Sm)[
                    StatusPill(StageStatusKind(selected.Stage), TasksWorkspaceState.StageTitle(selected.Stage)),
                    StatusPill(StatusKind.Idle, $"Owner: {selected.Owner}"),
                    StatusPill(StatusKind.Idle, $"Kind: {selected.Tag}")
                ],
                !string.IsNullOrWhiteSpace(selected.Description)
                    ? Text(selected.Description)
                    : MutedText("No additional task description recorded."),
            ];

            if (matchingFlow is not null)
            {
                detailNodes.Add(new JobCardNode(
                    matchingFlow.Title,
                    [.. matchingFlow.Steps.Select(step => StepNode(matchingFlow, step))],
                    matchingFlow.Fraction,
                    matchingFlow.Status.ToString().ToLowerInvariant()));

                List<Node?> flowActions =
                [
                    new Button("open-trace-details", "Open trace", Intent: OpenTrace(matchingFlow.TraceId)) { Small = true, Icon = "activity", AutomationName = "Open trace" },
                ];

                if (matchingFlow.Type == BuildFlow.Alias)
                {
                    flowActions.Add(Button("Show source", "code")
                        .Key("details:show-source")
                        .Small()
                        .OnClickAsync<TasksApp>(app => app.ShowSourceCommand(matchingFlow.FlowId)));
                    flowActions.Add(Button("Ask Ino to change", Icons.Sparkles)
                        .Key("details:ask-ino")
                        .Small()
                        .OnClickAsync<TasksApp>(app => app.AskInoCommand(matchingFlow.FlowId)));
                }

                detailNodes.Add(HStack(Space.Sm, [.. flowActions]));
            }

            if (state.Source is { Length: > 0 } src)
            {
                detailNodes.Add(Card("Deployed / Staged Unit Source", src));
            }

            sections.Add(Surface(SurfaceKind.Elevated, $"Task Details · {selected.Title}", $"ID: {selected.Id}")[
                VStack(Space.Sm, [.. detailNodes])
            ]);
        }
        else
        {
            sections.Add(Empty(
                "No task selected.",
                "Select any card on the Kanban board or row in the table to inspect its workflow details."));
        }

        sections.Add(RenderTimelineProjection(liveFlows, approvals, now));
        return VStack(Space.Md, [.. sections]);
    }

    private static string? ExtractBoundSha(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (HexShaRegex().Match(candidate) is { Success: true } match)
            {
                return match.Groups["sha"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![A-Fa-f0-9])(?<sha>[a-fA-F0-9]{40}|[a-fA-F0-9]{12,39})(?![A-Fa-f0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex HexShaRegex();

    private static Node RenderTimelineProjection(FlowView[] flows, Approval[] approvals, DateTimeOffset now)
    {
        var bars = TasksWorkspaceState.Bars(flows, now);
        if (bars.Length == 0)
        {
            return Surface(SurfaceKind.Subtle, "Dependency & Flow Timeline", "No live brain flows with timeline spans yet")[
                MutedText(approvals.Length == 0
                    ? "Start a plan or build flow to inspect its projected timeline and dependency lags."
                    : string.Create(CultureInfo.InvariantCulture, $"{approvals.Length} pending approval(s) in the brain inbox."))
            ];
        }

        var origin = bars.Min(b => b.Start);
        var byId = bars.ToDictionary(b => b.FlowId, StringComparer.Ordinal);
        double Day(DateTimeOffset at) => Math.Round((at - origin).TotalDays, 2);

        List<Node?> charts =
        [
            .. bars.Select(bar => (Node)BarChart(
                bar.Title,
                [("start", Day(bar.Start)), ("end", Day(bar.End))],
                seriesName: bar.FlowId)),
        ];

        foreach (var flow in flows)
        {
            foreach (var blocked in flow.Relations.Where(r => r.Kind == RelationKind.BlockedBy && byId.ContainsKey(r.TargetFlowId)))
            {
                var target = byId[blocked.TargetFlowId];
                charts.Add(LineChart(
                    $"{flow.Title} blocked by {target.Title}",
                    [("target end", Day(target.End)), ("start", Day(byId[flow.FlowId].Start))],
                    seriesName: "dependency"));
            }
        }

        return Surface(SurfaceKind.Elevated, "Flow Dependency Timeline", string.Create(CultureInfo.InvariantCulture, $"{bars.Length} projected span(s)"))[
            VStack(Space.Sm, [.. charts])
        ];
    }

    // ── Side Inspector View (Read-Only Approvals, Relations & Build Diagnostics) ──

    public static View RenderSideCore(TasksWorkspaceState state, FlowView[] flows, Approval[] approvals, string? source)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SelectedTaskId is not { } selected || flows.FirstOrDefault(f => f.FlowId == selected) is not { } flow)
        {
            if (state.SelectedTask is { } localTask)
            {
                return new View(localTask.Title, VStack(Space.Sm)[
                    StatusPill(StageStatusKind(localTask.Stage), TasksWorkspaceState.StageTitle(localTask.Stage)),
                    MutedText(string.IsNullOrWhiteSpace(localTask.Description) ? $"Owner: {localTask.Owner}" : localTask.Description)
                ]);
            }

            return new View("Task", Empty(
                state.SelectedTaskId is null ? "Select a task to see its steps." : "The task is not in the current list.",
                "Pick any card on the board or row in the list to inspect its steps, relations, and approvals."));
        }

        List<Node> children =
        [
            StatusPill(FlowStatusKind(flow.Status), flow.Status.ToString()),
            new JobCardNode(flow.Title, [.. flow.Steps.Select(step => StepNode(flow, step))], flow.Fraction, flow.Status.ToString().ToLowerInvariant()),
        ];

        if (flow.WaitingFor is { } waiting)
        {
            children.Add(Banner("Waiting", $"{waiting} on {flow.WaitingStep}", "warning"));
        }

        if (flow.Type == BuildFlow.Alias)
        {
            children.AddRange(BuildNodes(flow, approvals));
        }

        if (approvals.FirstOrDefault(a => a.Subject == flow.FlowId && a.Kind != ApprovalKind.Other) is { } approval)
        {
            var visual = VisualCatalog.ForApproval(approval.Subject);
            children.Add(new ApprovalCardNode("approval", approval.PlanId, approval.Title, approval.Detail, [], Icon: visual.IconKey, CapabilityLabel: visual.Label));
        }

        List<Node?> actions =
        [
            new Button("open-trace", "Open trace", Intent: OpenTrace(flow.TraceId)) { Small = true, Icon = "activity", AutomationName = "Open trace" },
        ];

        if (flow.Status is not (FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled))
        {
            actions.Add(DangerButton("Cancel").Key("cancel").Small().Emits(AppSignals.Emit(AppName, "cancel", flow.FlowId)));
        }

        if (flow.Type == BuildFlow.Alias)
        {
            actions.Add(Button("Show source", "code").Key("show-source").Small().Emits(AppSignals.Emit(AppName, "show-source", flow.FlowId)));
            actions.Add(Button("Ask Ino to change", Icons.Sparkles).Key("ask-ino-to-change").Small().Emits(AppSignals.Emit(AppName, "ask-ino", flow.FlowId)));
        }

        children.Add(HStack(Space.Sm, [.. actions]));
        children.Add(SelectBox(
            "Relation",
            state.RelateKind,
            [.. Enum.GetNames<RelationKind>().Select(kind =>
                Option(kind, kind).Key(kind).Emits(AppSignals.Emit(AppName, "relate-kind", kind)))]));
        children.Add(TextField(FieldKey("lag"), "Lag in days (Follows)").Key("lag"));
        children.Add(SelectBox(
            "Relate to",
            "",
            [.. flows.Where(f => f.FlowId != flow.FlowId).Select(f =>
                Option(f.FlowId, f.Title).Key(f.FlowId).Emits(AppSignals.Emit(AppName, "relate", flow.FlowId, state.RelateKind, f.FlowId, state.Draft.Lag)))]));

        if (source is not null)
        {
            children.Add(Card("Source", source));
        }

        return new View(flow.Title, VStack([.. children]));
    }

    private static IEnumerable<Node> BuildNodes(FlowView flow, Approval[] approvals)
    {
        if (flow is { Status: FlowStatus.Waiting, WaitingStep: "install" }
            && approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == flow.ConfirmationPlanId) is { } consent)
        {
            var visual = VisualCatalog.ForApproval(consent.Subject);
            yield return new ApprovalCardNode("consent", consent.PlanId, consent.Title, consent.Detail, [], Icon: visual.IconKey, CapabilityLabel: visual.Label);
            yield return MutedText("Approve or deny in the inbox.");
        }

        if (flow is { Status: FlowStatus.Waiting, WaitingStep: "deploy" })
        {
            var missing = approvals.FirstOrDefault(a => a.Kind == ApprovalKind.Other && a.Subject == flow.FlowId)?.Detail ?? flow.Detail;
            if (!string.IsNullOrWhiteSpace(missing))
            {
                yield return Banner(
                    "Needs a connection",
                    missing,
                    "warning",
                    Button("Open connections", "plug").Key("open-connections").Small().WithIntent(IntentKind.OpenSettings, "connections"));
            }
        }

        if (flow is { Status: FlowStatus.Failed, Detail: { Length: > 0 } detail } && FailedAt(flow) == "verify")
        {
            yield return Banner("Verify failed", TasksWorkspaceState.FirstLines(detail, 3), "danger");
        }
    }

    private static string? FailedAt(FlowView flow)
        => flow.Current ?? flow.Steps.FirstOrDefault(step => !flow.Done.Contains(step, StringComparer.Ordinal));

    private static JobStepNode StepNode(FlowView flow, string step)
    {
        if (flow.Done.Contains(step, StringComparer.Ordinal)) { return new JobStepNode($"✓ {step}", "done"); }
        if (flow.Current == step) { return new JobStepNode($"● {step}", "running", flow.WaitingStep == step ? $"{flow.WaitingFor}" : StepHint(flow.Type, step)); }
        return new JobStepNode($"○ {step}", "waiting", StepHint(flow.Type, step));
    }

    private static string? StepHint(string type, string step) => type != BuildFlow.Alias ? null : step switch
    {
        "author" => "Writes the spec, needs and scenarios",
        "draft" => "Writes the code",
        "verify" => "Runs the scenarios on fakes",
        "deploy" => "Binds connections and deploys",
        "install" => "Waits for your consent to the needs",
        _ => null,
    };

    private static StatusKind StageStatusKind(string stage) => TasksWorkspaceState.NormalizeStage(stage) switch
    {
        "done" => StatusKind.Done,
        "in-progress" => StatusKind.Working,
        "review" => StatusKind.NeedsYou,
        _ => StatusKind.Idle,
    };

    private static StatusKind FlowStatusKind(FlowStatus status) => status switch
    {
        FlowStatus.Completed => StatusKind.Done,
        FlowStatus.Running => StatusKind.Working,
        FlowStatus.Waiting => StatusKind.NeedsYou,
        FlowStatus.Failed or FlowStatus.Cancelled => StatusKind.Error,
        _ => StatusKind.Idle,
    };

    // ── Synapse Studio Source Inspection Helpers ─────────────────────────────

    private async Task<DeployedSource?> UnitOfAsync(string flowId)
    {
        var keys = new[] { AliasOf(flowId), FlowKey(flowId) }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            var units = await Get<ISynapseStudio>(SynapsesKey).List();
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
        var studio = Get<ISynapseStudio>(SynapsesKey);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        var unavailable = false;
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

        var ordered = files.Where(file => file.Key == "spec.md")
            .Concat(files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            .Concat(files.Where(file => file.Key is not "spec.md" and not "scenarios.json" && !file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            .Concat(files.Where(file => file.Key == "scenarios.json"));
        var listing = string.Concat(ordered.Select(file => $"\n\n--- {file.Key} ---\n{file.Value}"));
        return unavailable ? listing + "\n\n(files unavailable)" : listing;
    }

    private DateTimeOffset NowUtc()
    {
        if (Time is not null) { return Time.GetUtcNow(); }
        if (_injectedTime is not null) { return _injectedTime.GetUtcNow(); }
        try { return Context.Time.GetUtcNow(); }
        catch (InvalidOperationException) { return TimeProvider.System.GetUtcNow(); }
    }

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            _logger.LogDebug(error, "{Service} is optional when TasksApp runs in minimal test hosts.", typeof(T).Name);
            return null;
        }
    }

    private async Task TryFollowOptionalAsync<TNeuron, TSignal>(string key)
        where TNeuron : class
        where TSignal : Signal
    {
        try { await Follow<TNeuron, TSignal>(key); }
#pragma warning disable CA1031
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogDebug(error, "Following {Neuron} ({Key}) is optional.", typeof(TNeuron).Name, key);
        }
    }

    private static Node UiNeuron(string kind, string key) => new NeuronRef(kind, key);

    private static bool MatchesNeuronKey(string sourceId, string expectedKey)
        => string.Equals(sourceId, expectedKey, StringComparison.Ordinal)
            || sourceId.EndsWith("/" + expectedKey, StringComparison.Ordinal);

    private static string FlowKey(string flowId) => flowId[(flowId.LastIndexOf('/') + 1)..];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeGitHubConnectionKey(string? connectionKey)
    {
        if (string.IsNullOrWhiteSpace(connectionKey))
        {
            return "connection/github/main";
        }

        var trimmed = connectionKey.Trim();
        const string marker = "connection/github/";
        var idx = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var slug = trimmed[(idx + marker.Length)..].Trim('/');
            return slug.Length > 0 ? string.Concat(marker, slug) : "connection/github/main";
        }

        var rawSlug = trimmed.Trim('/');
        return rawSlug.Length > 0 ? string.Concat(marker, rawSlug) : "connection/github/main";
    }

    private async Task<string> StartEngineerFlowForIssueAsync(IFlows flows, string repoLabel, string taskTitle, string goal)
    {
        try
        {
            var request = new EngineerDelegationRequest(
                RepoSlug: repoLabel,
                Goal: goal,
                ParallelInstances: 1,
                Backend: "LocalGit",
                RequirePlanRatification: true);
            var summary = await flows.StartEngineerRun(request);
            if (!summary.Ok && !string.Equals(repoLabel, "os", StringComparison.OrdinalIgnoreCase))
            {
                summary = await flows.StartEngineerRun(request with { RepoSlug = "os" });
            }

            if (summary.Ok && !string.IsNullOrWhiteSpace(summary.FlowId))
            {
                return summary.FlowId;
            }
        }
        catch (NotSupportedException notSupported)
        {
            _logger.LogDebug(notSupported, "StartEngineerRun is unsupported on this IFlows host; starting flow.engineer directly.");
        }

        var fallbackStart = new EngineerStartRequest(
            RunId: string.Empty,
            Repository: "os",
            Goal: goal,
            InstanceCount: 1,
            RequirePlanRatification: true);
        return await fallbackStart.StartOnAsync(flows, title: taskTitle);
    }
}
