/*
# Tasks
Role: Default
Shipped: true
Operations: open, view, filter, select, new, cancel, relate-kind, relate, show-source, ask-ino, move, advance, complete, clear-done, approve-issue-draft, dismiss-issue-draft, escalate-issue

Workspace task planning & live brain flow orchestration (`app.tasks`) built on `AggregateScreen<TasksWorkspaceState>`
and `control.*` children (`IKanbanControl`, `IStepperControl`, `ITabStripControl`, `IQueryTableControl`).
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
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Tasks;

public sealed partial class TasksApp
{
    // ── Declarative View Composition (AppShellNode + control children) ───────

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

    // What the Tasks view needs from its screen to draw a control child: only the screen can render one.
    private sealed record TasksChildren(
        Func<string, TabStripProps, Node> Tabs,
        Func<string, KanbanProps, Node> Kanban,
        Func<string, StepperProps, Node> Stepper,
        Func<string, QueryTableProps, Node> Table);

    private TasksChildren Children => new(
        Child<ITabStripControl, TabStripProps>,
        Child<IKanbanControl, KanbanProps>,
        Child<IStepperControl, StepperProps>,
        Child<IQueryTableControl, QueryTableProps>);

    private static readonly KanbanColumnItem[] BoardColumns =
    [
        new("backlog", "Backlog", "list-todo"),
        new("in-progress", "In Progress", "play"),
        new("review", "Review", "eye"),
        new("done", "Done", "circle-check"),
    ];

    private static readonly QueryTableColumn[] TableColumns =
    [
        new("id", "ID"),
        new("title", "Title"),
        new("stage", "Stage"),
        new("owner", "Owner"),
        new("tag", "Kind"),
    ];

    // Tasks owns the tab: the strip draws it and signals the person's choice.
    private static TabStripProps TabsOf(TasksWorkspaceState state) => new(
        [new("board", "Board"), new("table", "Table"), new("approvals", "Approvals")],
        AutomationName: "Tasks views",
        SelectedId: TasksWorkspaceState.UiTabId(state.ActiveTab));

    // Tasks owns the cards and the selection: a move or a pick only signals, and Tasks renders the result back here.
    private static KanbanProps BoardOf(TasksWorkspaceState state, TaskWorkItem[] visible)
    {
        KanbanCardItem[] cards =
        [
            .. TasksWorkspaceState.StageIds.SelectMany(stage => visible
                .Where(t => string.Equals(t.Stage, stage, StringComparison.Ordinal))
                .Select(t => new KanbanCardItem(
                    t.Id,
                    stage,
                    t.Title,
                    string.IsNullOrWhiteSpace(t.Description) ? $"Owner: {t.Owner}" : t.Description,
                    t.Tag,
                    t.Owner))),
        ];
        var selected = cards.Any(c => string.Equals(c.Id, state.SelectedTaskId, StringComparison.Ordinal))
            ? state.SelectedTaskId
            : cards.FirstOrDefault()?.Id;
        return new KanbanProps("Tasks Board", BoardColumns, cards, SelectedCardId: selected);
    }

    // The selected task's lifecycle: one step per stage, the task's stage active. Choosing a step moves the task there.
    private static StepperProps LifecycleOf(TasksWorkspaceState state)
    {
        var activeStage = state.SelectedTask?.Stage ?? "backlog";
        var activeIdx = Math.Max(0, Array.IndexOf(TasksWorkspaceState.StageIds, activeStage));
        StepperItem[] steps =
        [
            .. TasksWorkspaceState.StageIds.Select((stageId, idx) => new StepperItem(
                stageId,
                TasksWorkspaceState.StageTitle(stageId),
                activeStage == "done" || idx < activeIdx ? StepperStatus.Done
                    : idx == activeIdx ? StepperStatus.Active
                    : StepperStatus.Pending)),
        ];
        var title = state.SelectedTask is { } task ? $"Lifecycle · {task.Title}" : "Task Lifecycle";
        return new StepperProps(steps, title, ActiveStepId: activeStage);
    }

    // The visible tasks as inline rows keyed by task id; the client filters, sorts and pages them by the table's own query.
    private static QueryTableProps TableOf(TasksWorkspaceState state, TaskWorkItem[] visible)
    {
        QueryTableRow[] rows =
        [
            .. visible.Select(t => new QueryTableRow(t.Id, [t.Id, t.Title, TasksWorkspaceState.StageTitle(t.Stage), t.Owner, t.Tag])),
        ];
        var selected = rows.Any(r => string.Equals(r.Id, state.SelectedTaskId, StringComparison.Ordinal)) ? state.SelectedTaskId : null;
        return new QueryTableProps("Workspace Tasks & Brain Flows", TableColumns, Rows: rows, SelectedRowId: selected);
    }

    private static Node RenderRootCore(
        TasksChildren controls,
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

        var toolbar = RenderTopBar(controls, state, approvals, issueDrafts);
        var sidebar = RenderSidebar(state, visible, liveFlows, approvals);
        var inspector = RenderRightInspector(controls, state, liveFlows, approvals, confirmations, issueDrafts);

        var body = activeTab switch
        {
            "table" or "list" => RenderFullBleedStage(controls.Table(TableKey, TableOf(state, visible)), lastRejection),
            "approvals" or "details" => RenderApprovalsAndDetailsTab(state, lastRejection, liveFlows, approvals, confirmations, issueDrafts, now),
            _ => RenderFullBleedStage(controls.Kanban(KanbanKey, BoardOf(state, visible)), lastRejection),
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
        TasksChildren controls,
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
            controls.Tabs(TabsKey, TabsOf(state)),
            (isAllFilter ? PrimaryButton("All") : GhostButton("All"))
                .Key("tasks:filter:all")
                .Small()
                .Style(OsMixStyles.FilterPill(isAllFilter))
                .Sends(new TasksFilter()),
            (isRunningFilter ? PrimaryButton("Active") : GhostButton("Active"))
                .Key("tasks:filter:running")
                .Small()
                .Style(OsMixStyles.FilterPill(isRunningFilter))
                .Sends(new TasksToggleStatusFilter("Running")),
            (isWaitingFilter ? PrimaryButton("Review") : GhostButton("Review"))
                .Key("tasks:filter:waiting")
                .Small()
                .Style(OsMixStyles.FilterPill(isWaitingFilter))
                .Sends(new TasksToggleStatusFilter("Waiting")),
            (isInoOwner ? PrimaryButton("Owner: ino") : GhostButton("Owner: ino"))
                .Key("tasks:filter:ino")
                .Small()
                .Style(OsMixStyles.FilterPill(isInoOwner))
                .Sends(new TasksToggleOwnerFilter("ino")),
            pendingGates > 0
                ? StatusPill(StatusKind.NeedsYou, string.Create(CultureInfo.InvariantCulture, $"{pendingGates} Gate(s)"))
                : null,
            FlexSpacer(),
            PrimaryButton("New Task", Icons.Plus)
                .Key("tasks:add")
                .Small()
                .Style(s => s.Elevation(UiElevation.Raised).Motion(UiMotion.Spring))
                .Sends(new TasksAddTask()),
            new Button("tasks:open-engineer", "Engineer ↗", Intent: AppSignals.OpenApp("engineer", state.SelectedFlowId))
            {
                Small = true,
                Icon = Icons.Sparkles,
                AutomationName = "Open Engineer",
                Style = OsMixStyles.LinkAction(),
            },
            new Button("tasks:open-creator", "Creator ↗", Intent: AppSignals.OpenApp("creator", state.SelectedFlowId))
            {
                Small = true,
                Icon = "git-branch",
                AutomationName = "Open Creator",
                Style = OsMixStyles.LinkAction(),
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
        var start = new TasksNew(state.Draft.Title, state.Draft.Description);
        var statusLine = approvals.Length == 0
            ? state.Status
            : string.Create(CultureInfo.InvariantCulture, $"{state.Status} · {approvals.Length} pending approval(s)");

        List<Node?> children =
        [
            SectionHeader("Quick Task", string.Create(CultureInfo.InvariantCulture, $"{visible.Length} visible")),
            TextField(FieldKey("title"), "Title").Key("title").Value(state.Draft.Title)
                .SendsOnChange(new TasksEditDraft("title")),
            TextField(FieldKey("description"), "Description").Key("description").Value(state.Draft.Description)
                .SendsOnChange(new TasksEditDraft("description")),
            PrimaryButton("Start", Icons.Plus).Key("start").Small().Sends(start),
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
                    .Sends(new TasksSelect(flow.FlowId))
                    .ToNode());
            }
        }

        children.Add(MutedText(statusLine));
        return VStack(Space.Sm, [.. children]);
    }

    private static Node RenderRightInspector(
        TasksChildren controls,
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
            controls.Stepper(StepperKey, LifecycleOf(state)),
            HStack(Space.Xs)[
                Button("Advance", "arrow-right")
                    .Key("tasks:advance")
                    .Small()
                    .Guard(state.CanAdvanceSelectedTask())
                    .Sends(new TasksAdvance()),
                Button("Complete", "circle-check")
                    .Key("tasks:complete")
                    .Small()
                    .Guard(state.CanCompleteSelectedTask())
                    .Sends(new TasksComplete()),
                GhostButton("Clear Done", "trash")
                    .Key("tasks:clear-done")
                    .Small()
                    .Guard(state.HasCompletedTasks())
                    .Sends(new TasksClearDone())
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
                    Style = OsMixStyles.LinkAction(),
                    Icon = "activity",
                    AutomationName = "Open trace",
                },
            ];

            if (matchingFlow.Type == BuildInner.Flow)
            {
                flowActions.Add(Button("Source", "code")
                    .Key("inspector:show-source")
                    .Small()
                    .Sends(new TasksShowSource(matchingFlow.FlowId)));
                flowActions.Add(Button("Ask Ino", Icons.Sparkles)
                    .Key("inspector:ask-ino")
                    .Small()
                    .Sends(new TasksAskIno(matchingFlow.FlowId)));
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
                    .Sends(new TasksApproveIssueDraft(draft.ConnectionKey, draft.IssueNumber, draft.PreviewId)),
                GhostButton("Dismiss", "x")
                    .Key($"{keyPrefix}tasks:dismiss-issue-draft:{draft.PreviewId}")
                    .Small()
                    .Sends(new TasksDismissIssueDraft(draft.ConnectionKey, draft.PreviewId)),
                Button("Fix in Studio ↗", Icons.Sparkles)
                    .Key($"{keyPrefix}tasks:escalate-issue:{draft.PreviewId}")
                    .Small()
                    .Sends(new TasksEscalateIssue(draft.ConnectionKey, draft.Repository, draft.IssueNumber, draft.PreviewId))
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
                    Style = OsMixStyles.LinkAction(),
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
                Style = OsMixStyles.LinkAction(),
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

            sections.Add(Surface(SurfaceKind.Elevated, "Pending Confirmations", string.Create(CultureInfo.InvariantCulture, $"{gateCards.Count} item(s)"))
                .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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
                string.Create(CultureInfo.InvariantCulture, $"{issueDrafts.Length} draft(s)"))
                .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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
                    new Button("open-trace-details", "Open trace", Intent: OpenTrace(matchingFlow.TraceId)) { Small = true, Style = OsMixStyles.LinkAction(), Icon = "activity", AutomationName = "Open trace" },
                ];

                if (matchingFlow.Type == BuildInner.Flow)
                {
                    flowActions.Add(Button("Show source", "code")
                        .Key("details:show-source")
                        .Small()
                        .Sends(new TasksShowSource(matchingFlow.FlowId)));
                    flowActions.Add(Button("Ask Ino to change", Icons.Sparkles)
                        .Key("details:ask-ino")
                        .Small()
                        .Sends(new TasksAskIno(matchingFlow.FlowId)));
                }

                detailNodes.Add(HStack(Space.Sm, [.. flowActions]));
            }

            if (state.Source is { Length: > 0 } src)
            {
                detailNodes.Add(Card("Deployed / Staged Unit Source", src).Style(OsMixStyles.Card()));
            }

            sections.Add(Surface(SurfaceKind.Elevated, $"Task Details · {selected.Title}", $"ID: {selected.Id}")
                .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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
            return Surface(SurfaceKind.Subtle, "Dependency & Flow Timeline", "No live brain flows with timeline spans yet")
                .Style(OsMixStyles.Surface(SurfaceKind.Subtle))[
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

        return Surface(SurfaceKind.Elevated, "Flow Dependency Timeline", string.Create(CultureInfo.InvariantCulture, $"{bars.Length} projected span(s)"))
            .Style(OsMixStyles.Surface(SurfaceKind.Elevated))[
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

        if (flow.Type == BuildInner.Flow)
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
            new Button("open-trace", "Open trace", Intent: OpenTrace(flow.TraceId)) { Small = true, Style = OsMixStyles.LinkAction(), Icon = "activity", AutomationName = "Open trace" },
        ];

        if (flow.Status is not (FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled))
        {
            actions.Add(DangerButton("Cancel").Key("cancel").Small().Sends(new TasksCancel(flow.FlowId)));
        }

        if (flow.Type == BuildInner.Flow)
        {
            actions.Add(Button("Show source", "code").Key("show-source").Small().Sends(new TasksShowSource(flow.FlowId)));
            actions.Add(Button("Ask Ino to change", Icons.Sparkles).Key("ask-ino-to-change").Small().Sends(new TasksAskIno(flow.FlowId)));
        }

        children.Add(HStack(Space.Sm, [.. actions]));
        children.Add(TextField(FieldKey("lag"), "Lag in days (Follows)").Key("lag").Value(state.Draft.Lag)
            .SendsOnChange(new TasksEditDraft("lag")));

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

    private static string? StepHint(string type, string step) => type != BuildInner.Flow ? null : step switch
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
}
