using System.Globalization;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

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
        return new View("Tasks", new Column(
        [
            new Tabs(
            [
                new TabItem("board", "Board", new ButtonAction(App, "view", ["board"]), "kanban"),
                new TabItem("list", "List", new ButtonAction(App, "view", ["list"]), "list"),
                new TabItem("timeline", "Timeline", new ButtonAction(App, "view", ["timeline"]), "gantt-chart"),
            ], state.View),
            RenderNewTask(state.Draft),
            new Text(approvals.Length == 0 ? state.Status : $"{state.Status} · {approvals.Length} pending approval(s)"),
            body,
        ]));
    }

    public static View RenderSide(TasksState state, FlowView[] flows, Approval[] approvals, string? source)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Selected is not { } selected || flows.FirstOrDefault(f => f.FlowId == selected) is not { } flow)
        {
            return new View("Task", new Text(state.Selected is null ? "Select a task to see its steps." : "The task is not in the current list."));
        }
        List<Node> children =
        [
            new JobCardNode(flow.Title, [.. flow.Steps.Select(step => StepNode(flow, step))], flow.Fraction, flow.Status.ToString().ToLowerInvariant()),
        ];
        if (flow.WaitingFor is { } waiting) { children.Add(new InfoBar("Waiting", $"{waiting} on {flow.WaitingStep}", "warning")); }
        if (approvals.FirstOrDefault(a => a.Subject == flow.FlowId) is { } approval)
        {
            children.Add(new Card(approval.Title, approval.Detail,
            [
                new Row(
                [
                    new Button("Approve", Resolve(approval.PlanId, true), Tone: ButtonTone.Accent) { Style = "primary" },
                    new Button("Deny", Resolve(approval.PlanId, false)),
                ]),
            ]));
        }
        List<Node> actions = [new Button("Open trace", OpenTrace(flow.TraceId)) { Small = true, Icon = "activity" }];
        if (flow.Status is not (FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled))
        {
            actions.Add(new Button("Cancel", new ButtonAction(App, "cancel", [flow.FlowId])) { Small = true, Style = "danger" });
        }
        if (flow.Type == "flow.build")
        {
            actions.Add(new Button("Show source", new ButtonAction(App, "show-source", [flow.FlowId])) { Small = true, Icon = "code" });
            actions.Add(new Button("Ask Ino to change", new ButtonAction(App, "ask-ino", [flow.FlowId])) { Small = true, Icon = "sparkles" });
        }
        children.Add(new Row(actions));
        children.Add(new Select("Relation", state.RelateKind,
            [.. Enum.GetNames<RelationKind>().Select(kind => new SelectOption(kind, kind, new ButtonAction(App, "relate-kind", [kind])))]));
        children.Add(new TextField(FieldKey("lag"), state.Draft.Lag, "Lag in days (Follows)"));
        children.Add(new Select("Relate to", "",
            [.. flows.Where(f => f.FlowId != flow.FlowId).Select(f => new SelectOption(f.FlowId, f.Title, new ButtonAction(App, "relate", [flow.FlowId, state.RelateKind, f.FlowId, state.Draft.Lag])))]));
        if (source is not null) { children.Add(new Card("Source", source, [])); }
        return new View(flow.Title, new Column(children));
    }

    // Opens the Trace app's window at the view trace/{traceId}.
    public static ButtonAction OpenTrace(string traceId) => new("apps/trace", "open", [$"trace/{traceId}"]);

    // A field is the textfield neuron apps/tasks/{field}; the app watches it.
    public static string FieldKey(string field) => $"apps/{App}/{field}";

    public static ButtonAction Resolve(string planId, bool approve)
        => new(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, [planId, approve ? "true" : "false"]);

    public static BoardColumn[] Columns(FlowView[] flows) =>
    [
        new("Pending", [.. flows.Where(f => f.Status == FlowStatus.Pending)]),
        new("Running", [.. flows.Where(f => f.Status == FlowStatus.Running)]),
        new("Waiting", [.. flows.Where(f => f.Status == FlowStatus.Waiting)]),
        new("Blocked", [.. flows.Where(f => f.Status == FlowStatus.Blocked)]),
        new("Completed", [.. flows.Where(f => f.Status == FlowStatus.Completed)]),
        new("Failed/Cancelled", [.. flows.Where(f => f.Status is FlowStatus.Failed or FlowStatus.Cancelled)]),
    ];

    // One bar per flow from Started to Ended, or to a projected end while it runs: twice the elapsed time over the done fraction
    // (at least 5%), at most 30 days. A Follows relation starts the bar its lag after the target's end, keeping the bar's length.
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
                // A cycle of Follows lays the repeated flow at its own times.
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
        var start = new ButtonAction(App, "new", [draft.Title, draft.Description]);
        return new Card("New task", "A plan flow drafts the subtasks from the description.",
        [
            new TextField(FieldKey("title"), draft.Title, "Title"),
            new TextField(FieldKey("description"), draft.Description, "Description") { OnSubmit = start },
            new Button("Start", start, Tone: ButtonTone.Accent) { Small = true, Icon = "plus" },
        ]);
    }

    // A lag typed as days ("1", "1.5d"); null when it is not.
    public static TimeSpan? ParseLag(string? text)
    {
        var value = text?.Trim().TrimEnd('d', 'D');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days >= 0 ? TimeSpan.FromDays(days) : null;
    }

    private static Node RenderBoard(FlowView[] flows)
    {
        var titles = flows.ToDictionary(f => f.FlowId, f => f.Title, StringComparer.Ordinal);
        return new Grid(6,
        [
            .. Columns(flows).Select(column => (Node)new Column(
            [
                new Heading(column.Name, 3, $"{column.Flows.Length}"),
                .. column.Flows.Select(flow => RenderBoardCard(flow, titles)),
            ])),
        ]);
    }

    private static Node RenderBoardCard(FlowView flow, Dictionary<string, string> titles)
    {
        List<Node> children = [new Progress("Progress", flow.Fraction, $"{flow.Done.Length}/{flow.Steps.Length}")];
        if (flow.WaitingFor is { } waiting) { children.Add(new Badge($"Waiting: {waiting}", "warning", "hourglass")); }
        foreach (var relation in flow.Relations)
        {
            var target = titles.TryGetValue(relation.TargetFlowId, out var title) ? title : relation.TargetFlowId;
            switch (relation.Kind)
            {
                case RelationKind.BlockedBy: children.Add(new Badge($"blocked by {target}", "danger", "lock")); break;
                case RelationKind.Follows: children.Add(new Badge($"after {target}{(relation.Lag is { } lag ? $" +{Lag(lag)}" : "")}", "neutral", "arrow-right")); break;
            }
        }
        children.Add(new Button("Open", new ButtonAction(App, "select", [flow.FlowId])) { Small = true });
        var icon = flow.Type[(flow.Type.LastIndexOf('.') + 1)..];
        return new Card(flow.Title, $"{icon} · {flow.Owner}{(flow.App is { } app ? $" / {app}" : "")}", children);
    }

    private static Node RenderList(FlowView[] flows) => new DataTable(
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

    // Chart is single-series: each flow is a columns chart with its start and end, in days from the earliest start; a BlockedBy
    // dependency is a line chart of two points, the target's end and the dependant's start.
    private static Node RenderTimeline(FlowView[] flows, DateTimeOffset now)
    {
        var bars = Bars(flows, now);
        if (bars.Length == 0) { return new Text("No tasks to lay out."); }
        var origin = bars.Min(b => b.Start);
        var byId = bars.ToDictionary(b => b.FlowId, StringComparer.Ordinal);
        double Day(DateTimeOffset at) => Math.Round((at - origin).TotalDays, 2);
        List<Node> charts = [.. bars.Select(bar => (Node)new Chart("columns", bar.Title,
            [new ChartPointNode("start", Day(bar.Start)), new ChartPointNode("end", Day(bar.End))], SeriesName: bar.FlowId))];
        foreach (var flow in flows)
        {
            foreach (var blocked in flow.Relations.Where(r => r.Kind == RelationKind.BlockedBy && byId.ContainsKey(r.TargetFlowId)))
            {
                var target = byId[blocked.TargetFlowId];
                charts.Add(new Chart("line", $"{flow.Title} blocked by {target.Title}",
                    [new ChartPointNode("target end", Day(target.End)), new ChartPointNode("start", Day(byId[flow.FlowId].Start))],
                    SeriesName: "dependency"));
            }
        }
        return new Column(charts);
    }

    private static JobStepNode StepNode(FlowView flow, string step)
    {
        if (flow.Done.Contains(step, StringComparer.Ordinal)) { return new JobStepNode($"✓ {step}", "done"); }
        if (flow.Current == step) { return new JobStepNode($"● {step}", "running", flow.WaitingStep == step ? $"{flow.WaitingFor}" : null); }
        return new JobStepNode($"○ {step}", "waiting");
    }

    private static string Lag(TimeSpan lag) => lag.TotalDays >= 1 ? $"{lag.TotalDays:0.#}d" : $"{lag.TotalHours:0.#}h";
}
