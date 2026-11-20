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
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Visuals;
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
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Tasks;

public sealed partial class TasksApp
{
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
}
