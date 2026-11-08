using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.TextField.Signals;
using IntoChat.OS.Apps.Tests.Unit.Desktop;
using IntoChat.OS.Apps.Desktop;

namespace IntoChat.OS.Apps.Tests.Unit.Tasks;

[Collection(nameof(TasksAppFacts))]
public sealed class TasksAppFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static FlowView Flow(string key, FlowStatus status, string type = "flow.note", FlowRelation[]? relations = null,
        DateTimeOffset? started = null, DateTimeOffset? ended = null, double fraction = 0.5, WaitReason? waiting = null)
        => new($"local/{type}/{key}", type, $"Task {key}", "owner", $"trace-{key}", status, ["draft", "approve", "start"], ["draft"],
            "approve", waiting, waiting is null ? null : "approve", null, fraction, relations ?? [], [], null, started ?? T0, ended, null, "ino");

    [Fact]
    public void BoardGroupsByStatus()
    {
        FlowView[] flows =
        [
            Flow("a", FlowStatus.Pending), Flow("b", FlowStatus.Running), Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval),
            Flow("d", FlowStatus.Blocked), Flow("e", FlowStatus.Completed), Flow("f", FlowStatus.Failed), Flow("g", FlowStatus.Cancelled),
        ];

        var columns = TasksViewRenderer.Columns(flows);

        Assert.Equal(["Pending", "Running", "Waiting", "Blocked", "Completed", "Failed/Cancelled"], columns.Select(c => c.Name));
        Assert.Equal(["Task f", "Task g"], columns[5].Flows.Select(f => f.Title));
        Assert.Equal("Task c", Assert.Single(columns[2].Flows).Title);
        var board = TasksViewRenderer.Render(new TasksState(), flows, [], T0);
        Assert.Contains("Waiting: Approval", Strings(board));
    }

    [Fact]
    public void TimelineLaysFollowsWithLag()
    {
        var target = Flow("t", FlowStatus.Completed, started: T0, ended: T0.AddDays(2));
        var dependant = Flow("d", FlowStatus.Pending, started: T0, ended: T0.AddDays(1),
            relations: [new FlowRelation(target.FlowId, RelationKind.Follows, TimeSpan.FromDays(1))]);

        var bars = TasksViewRenderer.Bars([target, dependant], T0.AddDays(3));

        var bar = Assert.Single(bars, b => b.FlowId == dependant.FlowId);
        Assert.Equal(T0.AddDays(3), bar.Start);
        Assert.Equal(T0.AddDays(4), bar.End);
        var view = TasksViewRenderer.Render(new TasksState("timeline", new FlowQuery(), null), [target, dependant], [], T0.AddDays(3));
        Assert.Contains(Nodes(view).OfType<Chart>(), c => c.SeriesName == dependant.FlowId);
    }

    [Fact]
    public void CardShowsStepsAndApproveDeny()
    {
        var flow = Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval);
        var approval = new Approval("plan-1", ApprovalKind.FlowStep, flow.FlowId, "Approve plan", "Three subtasks", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [approval], null);

        var strings = Strings(side).ToArray();
        Assert.Contains("✓ draft", strings);
        Assert.Contains("● approve", strings);
        Assert.Contains("○ start", strings);
        var buttons = Nodes(side).OfType<Button>().Select(b => b.Label).ToArray();
        Assert.Contains("Approve", buttons);
        Assert.Contains("Deny", buttons);
    }

    [Fact]
    public void ApproveButtonTargetsConfirmationsNeuron()
    {
        var flow = Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval);
        var approval = new Approval("plan-1", ApprovalKind.FlowStep, flow.FlowId, "Approve plan", "", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [approval], null);

        var approve = Assert.Single(Nodes(side).OfType<Button>(), b => b.Label == "Approve").Action;
        Assert.Equal(IConfirmations.ConfirmationsNeuronKey, approve.Neuron);
        Assert.Equal(IConfirmations.ResolveOperation, approve.Operation);
        Assert.Equal(["plan-1", "true"], approve.Arguments);
        var deny = Assert.Single(Nodes(side).OfType<Button>(), b => b.Label == "Deny").Action;
        Assert.Equal(["plan-1", "false"], deny.Arguments);
    }

    [Fact]
    public async Task NewTaskStartsPlanFlow()
    {
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();

        await tasks.Request("new", new TasksNew("Ship docs", "Write and publish the docs"));

        var started = Assert.Single(FakeFlows.Started);
        Assert.Equal(PlanFlow.Alias, started.Type);
        Assert.Equal("Ship docs", started.Title);
        Assert.Equal("Write and publish the docs", System.Text.Json.JsonDocument.Parse(started.InnerJson).RootElement.GetProperty("goal").GetString());
        Assert.Equal(started.Id, tasks.App.State.Selected);
    }

    [Fact]
    public async Task OpenTraceNavigatesToTraceApp()
    {
        var flow = Flow("c", FlowStatus.Running);
        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [], null);
        var open = Assert.Single(Nodes(side).OfType<Button>(), b => b.Label == "Open trace").Action;
        Assert.Equal("desktop", open.Neuron);
        Assert.Equal("open", open.Operation);

        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        os.Register<RecordingApp>();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request(open.Operation, new DesktopOpen(open.Arguments[0], open.Arguments[1]));

        Assert.Equal(("apps/trace", "open", "trace/trace-c"), Assert.Single(RecordingApp.Requests));
    }

    [Fact]
    public async Task NewTaskFormDeliversTitleAndDescription()
    {
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();

        await Field(tasks, "title", "Ship docs");
        await Field(tasks, "description", "Write and publish the docs");
        var start = Assert.Single(Nodes(tasks.App.Render()).OfType<Button>(), b => b.Label == "Start").Action;
        await tasks.Request(start.Operation, new TasksNew(start.Arguments[0], start.Arguments[1]));

        var started = Assert.Single(FakeFlows.Started);
        Assert.Equal("Ship docs", started.Title);
        Assert.Equal("Write and publish the docs", System.Text.Json.JsonDocument.Parse(started.InnerJson).RootElement.GetProperty("goal").GetString());
    }

    [Fact]
    public async Task CancelCallsFlows()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("c", FlowStatus.Running)];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        await tasks.Request("select", new TasksSelect(FakeFlows.Views[0].FlowId));

        var cancel = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Button>(), b => b.Label == "Cancel").Action;
        await tasks.Request(cancel.Operation, new TasksCancel(cancel.Arguments[0]));

        Assert.Equal(FakeFlows.Views[0].FlowId, Assert.Single(FakeFlows.Cancels));
    }

    [Fact]
    public async Task RelateAddsRelation()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("a", FlowStatus.Pending), Flow("b", FlowStatus.Running)];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        await tasks.Request("select", new TasksSelect(FakeFlows.Views[0].FlowId));

        var kind = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Select>(), s => s.Label == "Relation").Options.Single(o => o.Id == "Follows").Action!;
        await tasks.Request(kind.Operation, new TasksRelateKind(kind.Arguments[0]));
        await Field(tasks, "lag", "1");
        var target = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Select>(), s => s.Label == "Relate to").Options.Single().Action!;
        await tasks.Request(target.Operation, new TasksRelate(target.Arguments[0], target.Arguments[1], target.Arguments[2], target.Arguments[3]));

        var (flowId, relation) = Assert.Single(FakeFlows.Relations);
        Assert.Equal(FakeFlows.Views[0].FlowId, flowId);
        Assert.Equal(new FlowRelation(FakeFlows.Views[1].FlowId, RelationKind.Follows, TimeSpan.FromDays(1)), relation);
    }

    [Fact]
    public void ListFiltersByQuery()
    {
        FlowView[] flows = [Flow("a", FlowStatus.Running) with { Owner = "ann" }, Flow("b", FlowStatus.Running) with { Owner = "bob" }, Flow("c", FlowStatus.Completed) with { Owner = "ann" }];

        var view = TasksViewRenderer.Render(new TasksState("list", new FlowQuery(Owner: "ann", Status: FlowStatus.Running), null), flows, [], T0);

        var table = Assert.Single(Nodes(view).OfType<DataTable>());
        Assert.Equal("Task a", Assert.Single(table.Rows)[0]);
    }

    [Fact]
    public async Task ShowSourceRendersUnitMetadata()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("unit1", FlowStatus.Completed, type: "flow.build")];
        FakeSynapses.Sources = [new DeployedSource("unit1", "h1", ["weather"], DeployStatus.Deployed, null, T0)];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request("show-source", new TasksShowSource(FakeFlows.Views[0].FlowId));

        var source = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Card>(), c => c.Title == "Source");
        Assert.Contains("unit1", source.Body, StringComparison.Ordinal);
        Assert.Contains("h1", source.Body, StringComparison.Ordinal);
        Assert.Contains("weather", source.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlowsChangedRefreshesTheBoard()
    {
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        FakeFlows.Views = [Flow("late", FlowStatus.Running)];

        await Assert.Single(tasks.App.DeclaredWatches(), w => w.SignalType == typeof(FlowsChanged)).Handle(new FlowsChanged(FakeFlows.Views[0].FlowId));

        Assert.Contains("Task late", Strings(tasks.App.Render()));
    }

    // TestOS does not serve declared watches: hand the field's change to the watch on apps/tasks/{field}, as the host does.
    private static Task Field(InstalledApp<TasksApp> tasks, string field, string value)
        => Assert.Single(tasks.App.DeclaredWatches(), w => w.SignalType == typeof(TextFieldChanged) && w.Key(new AppAddress("local", "tasks")) == $"apps/tasks/{field}")
            .Handle(new TextFieldChanged($"local/apps/tasks/{field}", value));

    [Fact]
    public async Task AskInoToChangeWritesUiFocus()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("unit1", FlowStatus.Completed, type: "flow.build")];
        FakeSynapses.Sources = [new DeployedSource("unit1", "h1", ["weather"], DeployStatus.Deployed, null, T0)];
        var tasks = await os.Install<TasksApp>();

        await tasks.Request("ask-ino", new TasksAskIno(FakeFlows.Views[0].FlowId));

        var point = Assert.Single(FakeFocus.Points);
        Assert.Equal("window:ino", point.Target);
        Assert.Equal(FocusTone.Info, point.Tone);
        Assert.Equal("Change unit weather", point.Reason);
        Assert.Equal(new FocusAction("open", "ino"), point.Action);
    }

    private static TestOS FakeOs()
    {
        FakeFlows.Reset();
        FakeFocus.Points.Clear();
        FakeSynapses.Sources = [];
        var os = TestOS.Local();
        os.Register<FakeFlows>();
        os.Register<FakeApprovals>();
        os.Register<FakeFocus>();
        os.Register<FakeSynapses>();
        return os;
    }

    private static IEnumerable<Node> Nodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Nodes(p.GetValue(node)))],
        View view => Nodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Nodes),
        _ => [],
    };

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [],
        string text => [text],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Strings),
        Node or View or JobStepNode => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Strings(p.GetValue(value))),
        _ => [],
    };

    [Neuron("flows")]
    private sealed class FakeFlows : Neuron, IFlows
    {
        public static List<(string Type, string Title, string InnerJson, string Id)> Started { get; } = [];
        public static FlowView[] Views { get; set; } = [];

        public static List<string> Cancels { get; } = [];
        public static List<(string FlowId, FlowRelation Relation)> Relations { get; } = [];

        public static void Reset() { Started.Clear(); Cancels.Clear(); Relations.Clear(); Views = []; }

        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult(Views);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult(Views.FirstOrDefault(v => v.FlowId == flowId));
        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
        {
            var id = $"local/{type}/{Started.Count + 1}";
            Started.Add((type, title, innerJson, id));
            return Task.FromResult(id);
        }
        public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task Cancel(string flowId, string reason, CancellationToken ct = default) { Cancels.Add(flowId); return Task.CompletedTask; }
        public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default) { Relations.Add((flowId, relation)); return Task.CompletedTask; }
    }

    [Neuron("approvals")]
    private sealed class FakeApprovals : Neuron, IApprovals
    {
        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Approval>>([]);
    }

    [Neuron("ino.focus")]
    private sealed class FakeFocus : Neuron, IInoFocus
    {
        public static List<Focus> Points { get; } = [];

        public Task<InoToolResult> Point(string target, FocusTone tone, string reason, FocusAction? action = null)
        {
            Points.Add(new Focus(target, tone, reason, action));
            return Task.FromResult(new InoToolResult("Pointed."));
        }

        public Task<InoToolResult> Clear() => Task.FromResult(new InoToolResult("Cleared."));
    }

    [Neuron("ino.synapses")]
    private sealed class FakeSynapses : Neuron, ISynapseStudio
    {
        public static DeployedSource[] Sources { get; set; } = [];

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
        public Task<DeployedSource[]> List() => Task.FromResult(Sources);
    }
}
