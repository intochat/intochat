using System.Globalization;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Tasks;

public static class TasksViewRenderer
{
    private const string App = "tasks";
    // The text fields the app folds into TasksState.Draft, keyed apps/tasks/{field}.
    public static readonly string[] Fields = ["title", "description", "lag"];

    private static readonly TimeSpan MaxProjection = TimeSpan.FromDays(30);

    public static View Render(TasksState state, FlowView[] flows, Approval[] approvals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var shown = Filter(flows, state.Query);
        Node body = state.View switch
        {
            "list" => RenderList(shown),
            "timeline" => RenderTimeline(shown, now),
            _ => RenderBoard(shown),
        };

        var statusPill = approvals.Length > 0
            ? StatusPill(StatusKind.NeedsYou, $"{approvals.Length} Approval(s)")
            : StatusPill(StatusKind.Idle, $"{shown.Length} Flow(s)");

        return new View("Tasks", VStack(Space.Md)[
            HeaderBar("Tasks", "Plan and follow the brain's flows across board, list, and timeline", statusPill),
            Tabs(
                state.View,
                Tab("board", "Board", "kanban").Key("board").Emits(AppSignals.Emit(App, "view", "board")),
                Tab("list", "List", "list").Key("list").Emits(AppSignals.Emit(App, "view", "list")),
                Tab("timeline", "Timeline", "gantt-chart").Key("timeline").Emits(AppSignals.Emit(App, "view", "timeline"))),
            RenderNewTask(state.Draft),
            MutedText(approvals.Length == 0 ? state.Status : $"{state.Status} · {approvals.Length} pending approval(s)"),
            body
        ]);
    }

    public static View RenderSide(TasksState state, FlowView[] flows, Approval[] approvals, string? source)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Selected is not { } selected || flows.FirstOrDefault(f => f.FlowId == selected) is not { } flow)
        {
            return new View("Task", Empty(
                state.Selected is null ? "Select a task to see its steps." : "The task is not in the current list.",
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
            children.Add(new ApprovalCardNode("approval", approval.PlanId, approval.Title, approval.Detail, []));
        }

        List<Node?> actions =
        [
            new Button("open-trace", "Open trace", Intent: OpenTrace(flow.TraceId)) { Small = true, Icon = "activity" },
        ];
        if (flow.Status is not (FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled))
        {
            actions.Add(DangerButton("Cancel").Key("cancel").Small().Emits(AppSignals.Emit(App, "cancel", flow.FlowId)));
        }

        if (flow.Type == "flow.build")
        {
            actions.Add(Button("Show source", "code").Key("show-source").Small().Emits(AppSignals.Emit(App, "show-source", flow.FlowId)));
            actions.Add(Button("Ask Ino to change", Icons.Sparkles).Key("ask-ino-to-change").Small().Emits(AppSignals.Emit(App, "ask-ino", flow.FlowId)));
        }

        children.Add(HStack(Space.Sm, [.. actions]));
        children.Add(SelectBox(
            "Relation",
            state.RelateKind,
            [.. Enum.GetNames<RelationKind>().Select(kind =>
                Option(kind, kind).Key(kind).Emits(AppSignals.Emit(App, "relate-kind", kind)))]));
        children.Add(TextField(FieldKey("lag"), "Lag in days (Follows)").Key("lag"));
        children.Add(SelectBox(
            "Relate to",
            "",
            [.. flows.Where(f => f.FlowId != flow.FlowId).Select(f =>
                Option(f.FlowId, f.Title).Key(f.FlowId).Emits(AppSignals.Emit(App, "relate", flow.FlowId, state.RelateKind, f.FlowId, state.Draft.Lag)))]));

        if (source is not null)
        {
            children.Add(Card("Source", source));
        }

        return new View(flow.Title, VStack([.. children]));
    }

    private static StatusKind FlowStatusKind(FlowStatus status) => status switch
    {
        FlowStatus.Completed => StatusKind.Done,
        FlowStatus.Running => StatusKind.Working,
        FlowStatus.Waiting => StatusKind.NeedsYou,
        FlowStatus.Failed or FlowStatus.Cancelled => StatusKind.Error,
        _ => StatusKind.Idle,
    };

    private static IEnumerable<Node> BuildNodes(FlowView flow, Approval[] approvals)
    {
        if (flow is { Status: FlowStatus.Waiting, WaitingStep: "install" }
            && approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == flow.ConfirmationPlanId) is { } consent)
        {
            yield return new ApprovalCardNode("consent", consent.PlanId, consent.Title, consent.Detail, []);
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
            yield return Banner("Verify failed", FirstLines(detail, 3), "danger");
        }
    }

    private static string? FailedAt(FlowView flow) => flow.Current ?? flow.Steps.FirstOrDefault(step => !flow.Done.Contains(step, StringComparer.Ordinal));

    public static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= count ? string.Join('\n', lines) : string.Join('\n', lines.Take(count)) + $"\n(+{lines.Length - count} more)";
    }

    private static string? Hint(string type, string step) => type != BuildFlow.Alias ? null : step switch
    {
        "author" => "Writes the spec, needs and scenarios",
        "draft" => "Writes the code",
        "verify" => "Runs the scenarios on fakes",
        "deploy" => "Binds connections and deploys",
        "install" => "Waits for your consent to the needs",
        _ => null,
    };

    public static Intent OpenTrace(string traceId) => AppSignals.OpenApp("creator", $"trace/{traceId}");

    public static string FieldKey(string field) => $"apps/{App}/{field}";

    public static BoardColumn[] Columns(FlowView[] flows) =>
    [
        new("Pending", [.. flows.Where(f => f.Status == FlowStatus.Pending)]),
        new("Running", [.. flows.Where(f => f.Status == FlowStatus.Running)]),
        new("Waiting", [.. flows.Where(f => f.Status == FlowStatus.Waiting)]),
        new("Blocked", [.. flows.Where(f => f.Status == FlowStatus.Blocked)]),
        new("Completed", [.. flows.Where(f => f.Status == FlowStatus.Completed)]),
        new("Failed/Cancelled", [.. flows.Where(f => f.Status is FlowStatus.Failed or FlowStatus.Cancelled)]),
    ];

    public static TimelineBar[] Bars(FlowView[] flows, DateTimeOffset now)
    {
        var byId = flows.ToDictionary(f => f.FlowId, StringComparer.Ordinal);
        var laid = new Dictionary<string, TimelineBar>(StringComparer.Ordinal);
        var laying = new HashSet<string>(StringComparer.Ordinal);

        TimelineBar Lay(FlowView flow)
        {
            if (laid.TryGetValue(flow.FlowId, out var done)) { return done; }
            laying.Add(flow.FlowId);
            var start = flow.Started;
            var end = flow.Ended ?? Projected(flow, now);
            foreach (var follows in flow.Relations.Where(r => r.Kind == RelationKind.Follows))
            {
                if (!byId.TryGetValue(follows.TargetFlowId, out var target) || laying.Contains(target.FlowId)) { continue; }
                var after = Lay(target).End + (follows.Lag ?? TimeSpan.Zero);
                if (after > start) { end += after - start; start = after; }
            }

            laying.Remove(flow.FlowId);
            return laid[flow.FlowId] = new TimelineBar(flow.FlowId, flow.Title, start, end);
        }

        return [.. flows.Select(Lay)];
    }

    private static DateTimeOffset Projected(FlowView flow, DateTimeOffset now)
    {
        var elapsed = now > flow.Started ? now - flow.Started : TimeSpan.Zero;
        var span = TimeSpan.FromTicks((long)Math.Min(MaxProjection.Ticks, 2 * elapsed.Ticks / Math.Max(flow.Fraction, 0.05)));
        return flow.Started + span;
    }

    private static FlowView[] Filter(FlowView[] flows, FlowQuery query) =>
    [
        .. flows.Where(f => (query.Owner is null || f.Owner == query.Owner)
            && (query.Status is null || f.Status == query.Status)
            && (query.Type is null || f.Type == query.Type)),
    ];

    private static Node RenderNewTask(TasksDraft draft)
    {
        var start = AppSignals.Emit(App, "new", draft.Title, draft.Description);
        return Card("New task", "A plan flow drafts the subtasks from the description.")[
            TextField(FieldKey("title"), "Title").Key("title"),
            TextField(FieldKey("description"), "Description").Key("description").Emits(start),
            PrimaryButton("Start", Icons.Plus).Key("start").Small().Emits(start)
        ];
    }

    public static TimeSpan? ParseLag(string? text)
    {
        var value = text?.Trim().TrimEnd('d', 'D');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days >= 0 ? TimeSpan.FromDays(days) : null;
    }

    private static Node RenderBoard(FlowView[] flows)
    {
        var titles = flows.ToDictionary(f => f.FlowId, f => f.Title, StringComparer.Ordinal);
        return Grid(6, Space.Md,
        [
            .. Columns(flows).Select(column => (Node)VStack(Space.Sm,
            [
                Heading(column.Name, 3, $"{column.Flows.Length}"),
                .. column.Flows.Select(flow => RenderBoardCard(flow, titles)),
            ])),
        ]);
    }

    private static Node RenderBoardCard(FlowView flow, Dictionary<string, string> titles)
    {
        List<Node?> children = [new Progress("Progress", flow.Fraction, $"{flow.Done.Length}/{flow.Steps.Length}")];
        if (flow.WaitingFor is { } waiting)
        {
            children.Add(StatusPill(StatusKind.NeedsYou, $"Waiting: {waiting}"));
        }

        foreach (var relation in flow.Relations)
        {
            var target = titles.TryGetValue(relation.TargetFlowId, out var title) ? title : relation.TargetFlowId;
            switch (relation.Kind)
            {
                case RelationKind.BlockedBy:
                    children.Add(StatusPill(StatusKind.Error, $"blocked by {target}"));
                    break;
                case RelationKind.Follows:
                    children.Add(StatusPill(StatusKind.Idle, $"after {target}{(relation.Lag is { } lag ? $" +{Lag(lag)}" : "")}"));
                    break;
            }
        }

        children.Add(Button("Open").Key($"open:{flow.FlowId}").Small().Emits(AppSignals.Emit(App, "select", flow.FlowId)));
        var icon = flow.Type[(flow.Type.LastIndexOf('.') + 1)..];
        return Card(flow.Title, $"{icon} · {flow.Owner}{(flow.App is { } app ? $" / {app}" : "")}", [.. children]);
    }

    private static Node RenderList(FlowView[] flows) => Table(
        "Tasks",
        ["Title", "Type", "Status", "Owner", "Started", "Ended", "Progress"],
        [
            .. flows.OrderByDescending(f => f.Started).Select(f => (IReadOnlyList<string>)
            [
                f.Title, f.Type, f.Status.ToString(), f.Owner,
                f.Started.ToString("u", CultureInfo.InvariantCulture),
                f.Ended?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                f.Fraction.ToString("P0", CultureInfo.InvariantCulture),
            ]),
        ],
        "neuron://local/flows/flows/index");

    private static Node RenderTimeline(FlowView[] flows, DateTimeOffset now)
    {
        var bars = Bars(flows, now);
        if (bars.Length == 0)
        {
            return Empty("No tasks to lay out.", "Start a plan or build flow to inspect its timeline projection.");
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

        return VStack([.. charts]);
    }

    private static JobStepNode StepNode(FlowView flow, string step)
    {
        if (flow.Done.Contains(step, StringComparer.Ordinal)) { return new JobStepNode($"✓ {step}", "done"); }
        if (flow.Current == step) { return new JobStepNode($"● {step}", "running", flow.WaitingStep == step ? $"{flow.WaitingFor}" : Hint(flow.Type, step)); }
        return new JobStepNode($"○ {step}", "waiting", Hint(flow.Type, step));
    }

    private static string Lag(TimeSpan lag) => lag.TotalDays >= 1 ? $"{lag.TotalDays:0.#}d" : $"{lag.TotalHours:0.#}h";
}
