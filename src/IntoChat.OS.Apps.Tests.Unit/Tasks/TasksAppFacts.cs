using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

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
    public void OpenTraceNavigatesToTraceApp()
    {
        var flow = Flow("c", FlowStatus.Running);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [], null);

        var open = Assert.Single(Nodes(side).OfType<Button>(), b => b.Label == "Open trace").Action;
        Assert.Equal(TasksViewRenderer.OpenTrace("trace-c"), open);
        Assert.Equal("desktop", open.Neuron);
        Assert.Equal("open", open.Operation);
        Assert.Equal(["trace", "trace/trace-c"], open.Arguments);
    }

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

        public static void Reset() { Started.Clear(); Views = []; }

        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult(Views);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult(Views.FirstOrDefault(v => v.FlowId == flowId));
        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
        {
            var id = $"local/{type}/{Started.Count + 1}";
            Started.Add((type, title, innerJson, id));
            return Task.FromResult(id);
        }
        public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task Cancel(string flowId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default) => Task.CompletedTask;
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
