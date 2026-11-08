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
                new TabItem("board", "board", "Board", AppSignals.Emit(App, "view", "board"), "kanban"),
                new TabItem("list", "list", "List", AppSignals.Emit(App, "view", "list"), "list"),
                new TabItem("timeline", "timeline", "Timeline", AppSignals.Emit(App, "view", "timeline"), "gantt-chart"),
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
        if (flow.Type == BuildFlow.Alias) { children.AddRange(BuildNodes(flow, approvals)); }
        if (approvals.FirstOrDefault(a => a.Subject == flow.FlowId && a.Kind != ApprovalKind.Other) is { } approval)
        {
            children.Add(new ApprovalCardNode("approval", approval.PlanId, approval.Title, approval.Detail, [], Resolve: new Resolve(approval.PlanId)));
        }
        List<Node> actions = [new Button("open-trace", "Open trace", Intent: OpenTrace(flow.TraceId)) { Small = true, Icon = "activity" }];
        if (flow.Status is not (FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled))
        {
            actions.Add(new Button("cancel", "Cancel", Emit: AppSignals.Emit(App, "cancel", flow.FlowId)) { Small = true, Style = "danger" });
        }
        if (flow.Type == "flow.build")
        {
            actions.Add(new Button("show-source", "Show source", Emit: AppSignals.Emit(App, "show-source", flow.FlowId)) { Small = true, Icon = "code" });
            actions.Add(new Button("ask-ino-to-change", "Ask Ino to change", Emit: AppSignals.Emit(App, "ask-ino", flow.FlowId)) { Small = true, Icon = "sparkles" });
        }
        children.Add(new Row(actions));
        children.Add(new Select("Relation", state.RelateKind,
            [.. Enum.GetNames<RelationKind>().Select(kind => new SelectOption(kind, kind, kind, AppSignals.Emit(App, "relate-kind", kind)))]));
        children.Add(new Field("lag", FieldKey("lag"), FieldKind.Text, "Lag in days (Follows)"));
        children.Add(new Select("Relate to", "",
            [.. flows.Where(f => f.FlowId != flow.FlowId).Select(f => new SelectOption(f.FlowId, f.FlowId, f.Title, AppSignals.Emit(App, "relate", flow.FlowId, state.RelateKind, f.FlowId, state.Draft.Lag)))]));
        if (source is not null) { children.Add(new Card("Source", source, [])); }
        return new View(flow.Title, new Column(children));
    }

    // A unit flow's own cards: the consent its install waits on (the needs sentence; answered in the OS inbox, so no resolve here), the
    // connection its deploy waits for, and the verify failures of a flow that failed there.
    private static IEnumerable<Node> BuildNodes(FlowView flow, Approval[] approvals)
    {
        if (flow is { Status: FlowStatus.Waiting, WaitingStep: "install" }
            && approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == flow.ConfirmationPlanId) is { } consent)
        {
            yield return new ApprovalCardNode("consent", consent.PlanId, consent.Title, consent.Detail, []);
            yield return new Text("Approve or deny in the inbox.") { Tone = "muted" };
        }
        if (flow is { Status: FlowStatus.Waiting, WaitingStep: "deploy" })
        {
            var missing = approvals.FirstOrDefault(a => a.Kind == ApprovalKind.Other && a.Subject == flow.FlowId)?.Detail ?? flow.Detail;
            if (!string.IsNullOrWhiteSpace(missing))
            {
                yield return new InfoBar("Needs a connection", missing, "warning",
                    new Button("open-connections", "Open connections", Intent: new Intent(IntentKind.OpenSettings, "connections")) { Small = true, Icon = "plug" });
            }
        }
        if (flow is { Status: FlowStatus.Failed, Detail: { Length: > 0 } detail } && FailedAt(flow) == "verify")
        {
            yield return new InfoBar("Verify failed", FirstLines(detail, 3), "danger");
        }
    }

    // The step a failed flow stopped at: the current one, else the first not done.
    private static string? FailedAt(FlowView flow) => flow.Current ?? flow.Steps.FirstOrDefault(step => !flow.Done.Contains(step, StringComparer.Ordinal));

    // The first lines of a transcript and how many follow.
    public static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= count ? string.Join('\n', lines) : string.Join('\n', lines.Take(count)) + $"\n(+{lines.Length - count} more)";
    }

    // What a unit flow's step does, shown beside a step that has not finished.
    private static string? Hint(string type, string step) => type != BuildFlow.Alias ? null : step switch
    {
        "author" => "Writes the spec, needs and scenarios",
        "draft" => "Writes the code",
        "verify" => "Runs the scenarios on fakes",
        "deploy" => "Binds connections and deploys",
        "install" => "Waits for your consent to the needs",
        _ => null,
    };

    // Opens the Creator at the trace: its inspector shows trace/{traceId}.
    public static Intent OpenTrace(string traceId) => AppSignals.OpenApp("creator", $"trace/{traceId}");

    // A field is the textfield neuron apps/tasks/{field}; the app watches it.
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
        var start = AppSignals.Emit(App, "new", draft.Title, draft.Description);
        return new Card("New task", "A plan flow drafts the subtasks from the description.",
        [
            new Field("title", FieldKey("title"), FieldKind.Text, "Title"),
            new Field("description", FieldKey("description"), FieldKind.Text, "Description", start),
            new Button("start", "Start", Emit: start, Tone: ButtonTone.Accent) { Small = true, Icon = "plus" },
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
        children.Add(new Button("open", "Open", Emit: AppSignals.Emit(App, "select", flow.FlowId)) { Small = true });
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
        if (flow.Current == step) { return new JobStepNode($"● {step}", "running", flow.WaitingStep == step ? $"{flow.WaitingFor}" : Hint(flow.Type, step)); }
        return new JobStepNode($"○ {step}", "waiting", Hint(flow.Type, step));
    }

    private static string Lag(TimeSpan lag) => lag.TotalDays >= 1 ? $"{lag.TotalDays:0.#}d" : $"{lag.TotalHours:0.#}h";
}
