using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Fields;

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
    public void CardShowsStepsAsReadOnlyWithoutResolveAction()
    {
        var flow = Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval);
        var approval = new Approval("plan-1", ApprovalKind.FlowStep, flow.FlowId, "Approve plan", "Three subtasks", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [approval], null);

        var strings = Strings(side).ToArray();
        Assert.Contains("✓ draft", strings);
        Assert.Contains("● approve", strings);
        Assert.Contains("○ start", strings);
        Assert.Null(Assert.Single(Nodes(side).OfType<ApprovalCardNode>()).Resolve);
    }

    [Fact]
    public void RenderSideApprovalCardIsReadOnlyWithoutResolveAction()
    {
        var flow = Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval);
        var approval = new Approval("plan-1", ApprovalKind.FlowStep, flow.FlowId, "Approve plan", "Three subtasks", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [approval], null);

        var card = Assert.Single(Nodes(side).OfType<ApprovalCardNode>());
        Assert.Null(card.Resolve);
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

    // The presentation turns this action into os://window/open?app=creator&input=trace%2Ftrace-c (AppPresentationForeignActionFacts).
    [Fact]
    public void OpenTraceNavigatesToTheCreator()
    {
        var flow = Flow("c", FlowStatus.Running);
        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [], null);

        var open = Assert.Single(Nodes(side).OfType<Button>(), b => b.Label == "Open trace").Intent;

        Assert.Equal(new Intent(IntentKind.OpenWindow, "os://window/open?app=creator&input=trace%2Ftrace-c"), open);
    }

    [Fact]
    public void FormFieldsAreTheAppsTextfieldNeurons()
    {
        var names = Nodes(TasksViewRenderer.Render(new TasksState(), [], [], T0)).OfType<Field>().Select(f => f.Name).ToArray();
        Assert.Equal(["apps/tasks/title", "apps/tasks/description"], names);
    }

    [Fact]
    public async Task NewTaskFormDeliversTitleAndDescription()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();

        await Field(tasks, "title", "Ship docs");
        await Field(tasks, "description", "Write and publish the docs");
        var start = Assert.Single(Nodes(tasks.App.Render()).OfType<Button>(), b => b.Label == "Start").Command!;
        var startArgs = EmitArgs.Of(start);
        await tasks.Request(start.Signal, new TasksNew(startArgs[0], startArgs[1]));

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

        var cancel = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Button>(), b => b.Label == "Cancel").Command!;
        await tasks.Request(cancel.Signal, new TasksCancel(EmitArgs.Of(cancel)[0]));

        Assert.Equal(FakeFlows.Views[0].FlowId, Assert.Single(FakeFlows.Cancels));
    }

    [Fact]
    public async Task RefusedCancelShowsWhy()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval)];
        var flowId = FakeFlows.Views[0].FlowId;
        FakeFlows.CancelRefusal = $"flow {flowId} waits for a person at approve: only a person cancels it";
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request("cancel", new TasksCancel(flowId));

        Assert.Empty(FakeFlows.Cancels);
        Assert.Contains($"Could not cancel: {FakeFlows.CancelRefusal}", Strings(tasks.App.Render()));
    }

    [Fact]
    public async Task RelateAddsRelation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("a", FlowStatus.Pending), Flow("b", FlowStatus.Running)];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        await tasks.Request("select", new TasksSelect(FakeFlows.Views[0].FlowId));

        var kind = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Select>(), s => s.Label == "Relation").Options.Single(o => o.Id == "Follows").Emit!;
        await tasks.Request(kind.Signal, new TasksRelateKind(EmitArgs.Of(kind)[0]));
        await Field(tasks, "lag", "1");
        var target = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Select>(), s => s.Label == "Relate to").Options.Single().Emit!;
        var targetArgs = EmitArgs.Of(target);
        await tasks.Request(target.Signal, new TasksRelate(targetArgs[0], targetArgs[1], targetArgs[2], targetArgs[3]));

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

    private static FlowView Unit(FlowStatus status, string? current, string[] done, WaitReason? waiting = null, string? waitingStep = null, string? plan = null, string? detail = null)
        => Flow("u", status, type: "flow.build") with
        {
            Steps = ["author", "draft", "verify", "deploy", "install"],
            Done = done,
            Current = current,
            WaitingFor = waiting,
            WaitingStep = waitingStep,
            ConfirmationPlanId = plan,
            Detail = detail,
        };

    [Fact]
    public void AnInstallWaitShowsTheNeedsSentenceAndNoApproveButton()
    {
        var flow = Unit(FlowStatus.Waiting, "install", ["author", "draft", "verify", "deploy"], WaitReason.External, "install", "plan-i");
        var consent = new Approval("plan-i", ApprovalKind.SynapseStaged, "xwatch", "Install xwatch?", "Reads posts. Reads api.x.com.", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [consent], null);

        Assert.Contains("Reads posts. Reads api.x.com.", Strings(side));
        Assert.Contains("✓ deploy", Strings(side));
        Assert.Contains("● install", Strings(side));
        Assert.All(Nodes(side).OfType<ApprovalCardNode>(), card => Assert.Null(card.Resolve));
        Assert.DoesNotContain(Nodes(side).OfType<Button>(), b => b.Label.Contains("pprove", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingConnectionShowsTheConnectTextWithAnOpenSettingsIntent()
    {
        var flow = Unit(FlowStatus.Waiting, "deploy", ["author", "draft", "verify"], WaitReason.External, "deploy", detail: "connect http@1 for api.x.com");

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [], null);

        Assert.Contains("connect http@1 for api.x.com", Strings(side));
        var open = Assert.Single(Nodes(side).OfType<Button>(), b => b.Key == "open-connections").Intent;
        Assert.Equal(new Intent(IntentKind.OpenSettings, "connections"), open);
    }

    [Fact]
    public void AFailedVerifyShowsTheFirstThreeLinesAndTheCount()
    {
        var flow = Unit(FlowStatus.Failed, "verify", ["author", "draft"],
            detail: "The unit did not pass verify.\nX.cs(3) SYN001: no\nscenario one failed\nscenario two failed\nscenario three failed");

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [], null);

        var bar = Assert.Single(Nodes(side).OfType<InfoBar>(), b => b.Title == "Verify failed");
        Assert.Equal("The unit did not pass verify.\nX.cs(3) SYN001: no\nscenario one failed\n(+2 more)", bar.Message);
    }

    [Fact]
    public async Task ShowSourceRendersTheSpecFirstThenTheCodeThenTheScenarios()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("unit1", FlowStatus.Completed, type: "flow.build")];
        FakeSynapses.Sources = [new DeployedSource("unit1", "h1", ["weather"], DeployStatus.Deployed, null, T0)];
        FakeSynapses.Files = new() { ["B.cs"] = "class B;", ["A.cs"] = "class A;" };
        FakeSynapses.SidecarFiles = new() { ["scenarios.json"] = "[]", ["spec.md"] = "# Weather" };
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request("show-source", new TasksShowSource(FakeFlows.Views[0].FlowId));

        var body = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Card>(), c => c.Title == "Source").Body;
        var order = new[] { "--- spec.md ---", "--- A.cs ---", "--- B.cs ---", "--- scenarios.json ---" }.Select(marker => body.IndexOf(marker, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0, body));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void AMissingConnectionApprovalIsOneReadOnlyCard()
    {
        var flow = Unit(FlowStatus.Waiting, "deploy", ["author", "draft", "verify"], WaitReason.External, "deploy");
        var other = new Approval("plan-o", ApprovalKind.Other, flow.FlowId, "Connect", "connect http@1 for api.x.com", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksViewRenderer.RenderSide(new TasksState("board", new FlowQuery(), flow.FlowId), [flow], [other], null);

        Assert.All(Nodes(side).OfType<ApprovalCardNode>(), card => Assert.Null(card.Resolve));
        Assert.Single(Nodes(side).OfType<InfoBar>(), b => b.Title == "Needs a connection");
        Assert.Contains("connect http@1 for api.x.com", Strings(side));
    }

    [Fact]
    public async Task ShowSourceOfACompletedBuildFollowsItsSubject()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("done", FlowStatus.Completed, type: "flow.build") with { Subject = "x.watch" }];
        FakeSynapses.Sources = [new DeployedSource("x.watch", "h2", ["x.watch"], DeployStatus.Deployed, null, T0)];
        FakeSynapses.Files = new() { ["XWatch.cs"] = "class XWatch;" };
        FakeSynapses.SidecarFiles = new() { ["spec.md"] = "# Watches mentions" };
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request("show-source", new TasksShowSource(FakeFlows.Views[0].FlowId));

        var body = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Card>(), c => c.Title == "Source").Body;
        Assert.True(body.IndexOf("--- spec.md ---", StringComparison.Ordinal) is >= 0 and var spec && spec < body.IndexOf("--- XWatch.cs ---", StringComparison.Ordinal), body);
    }

    [Fact]
    public async Task FlowsChangedRefreshesTheBoard()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        FakeFlows.Views = [Flow("late", FlowStatus.Running)];

        await tasks.App.OnFlowsChanged(new FlowsChanged(FakeFlows.Views[0].FlowId));

        Assert.Contains("Task late", Strings(tasks.App.Render()));
    }

    private static async Task Field(InstalledApp<TasksApp> tasks, string field, string value)
    {
        FakeField.Values[$"apps/tasks/{field}"] = value;
        await tasks.App.OnField(field, new FieldChanged("local/apps/tasks", field, 1));
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
        FakeSynapses.SidecarFiles = [];
        FakeSynapses.Files = [];
        var os = TestOS.Local();
        os.Register<FakeFlows>();
        os.Register<FakeApprovals>();
        os.Register<FakeFocus>();
        os.Register<FakeSynapses>();
        os.Register<FakeField>();
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

        // The message of the rule the index refuses a cancel with, when set.
        public static string? CancelRefusal { get; set; }

        public static void Reset() { Started.Clear(); Cancels.Clear(); Relations.Clear(); Views = []; CancelRefusal = null; }

        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult(Views);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult(Views.FirstOrDefault(v => v.FlowId == flowId));
        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
        {
            var id = $"local/{type}/{Started.Count + 1}";
            Started.Add((type, title, innerJson, id));
            return Task.FromResult(id);
        }
        public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task Cancel(string flowId, string reason, CancellationToken ct = default)
        {
            if (CancelRefusal is { } message) { throw new DigitalBrain.Rules.RuleViolationException(new DigitalBrain.Rules.Rejected("Cancel", ["apps.flows-person-cancels-approval-wait"], [message])); }
            Cancels.Add(flowId);
            return Task.CompletedTask;
        }
        public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default) { Relations.Add((flowId, relation)); return Task.CompletedTask; }
    }

    [Neuron("ui.field")]
    private sealed class FakeField : Neuron, IField
    {
        public static Dictionary<string, string?> Values { get; } = [];

        public Task Configure(FieldKind kind) => Task.CompletedTask;
        public Task Set(string? value) { Values[Id.Key] = value; return Task.CompletedTask; }
        public Task Input(string value) => Set(value);
        public Task<string?> TakeSecret() => Task.FromResult<string?>(null);
        public Task<FieldState> Read() => Task.FromResult(new FieldState { Value = Values.GetValueOrDefault(Id.Key) });
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
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings) => Task.FromResult(new UnitDeployResult(true, "", DigitalBrain.Units.DeployStatus.Deployed, null, new Dictionary<string, DigitalBrain.Units.InstallOutcome>()));
        public static Dictionary<string, string> SidecarFiles { get; set; } = [];
        public static Dictionary<string, string> Files { get; set; } = [];

        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(SidecarFiles);
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(Files);
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult($"{alias} has no recorded failures.", Ok: true));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
        public Task<DeployedSource[]> List() => Task.FromResult(Sources);
    }
}
