using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

[Collection(nameof(CreatorTimeTravelFacts))]
public sealed class CreatorTimeTravelFacts
{
    private const string Unit = "local/ctr/default";
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    // The time-travel slider, the inspector tabs and the signals table are control.* children of Creator: Creator owns the cursor, the
    // tab and the rows, draws them as props, and follows each child's ValueChanged, SelectionChanged and RowSelected. The state tab is
    // a jsonTree data node and the source tab a code block, both drawn from the inspection Creator keeps in its stored state.
    [Fact]
    public async Task TimeTravelStepsOneEventAtATimeAndTheSliderChildScrubsTheCursor()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen("trace/t-travel"));
        await os.Idle();
        Assert.Equal("signals", creator.App.State.InspectorTab);
        await Send(creator, CreatorView.InspectorTabsKey, new Select("state"));

        // Initially live at the latest state (bumps: 2), with no ui.* control mounted anywhere.
        Assert.Null(creator.App.State.ReplayCursor);
        Assert.Equal(4, creator.App.State.ReplayStepCount);
        Assert.Equal(2, Bumps(creator.App.Render()));
        Assert.DoesNotContain(AllNodes(creator.App.Render()).OfType<NeuronRef>(), n => n.Kind != "slot");
        var live = PropsOf<SliderProps>(creator.App.Render(), CreatorView.ReplaySliderKey);
        Assert.Equal((1d, 4d, 4d, true), (live.Min, live.Max, live.Value!.Value, live.IsEnabled));
        Assert.Equal("Live · Event 4 / 4", live.Label);

        // Space -> pause / toggle into Replay at the latest event (index 3 -> Event 4 / 4)
        await creator.Request("replay", new CreatorReplay("space"));
        Assert.Equal(3L, creator.App.State.ReplayCursor);
        Assert.Equal(4d, PropsOf<SliderProps>(creator.App.Render(), CreatorView.ReplaySliderKey).Value);

        // ArrowLeft twice -> index 1 (StateChanged: bumps = 1)
        await creator.Request("replay", new CreatorReplay("arrowleft"));
        await creator.Request("replay", new CreatorReplay("arrowleft"));
        Assert.Equal(1L, creator.App.State.ReplayCursor);
        Assert.Equal("Event 2 / 4", creator.App.State.Status);
        Assert.Equal(2d, PropsOf<SliderProps>(creator.App.Render(), CreatorView.ReplaySliderKey).Value);
        Assert.Equal(1, Bumps(creator.App.Render()));
        Assert.Equal("State · Event 2 / 4", JsonTreeOf(creator.App.Render()).Title);

        // Home -> first event, ArrowRight -> second, End -> latest (bumps = 2)
        await creator.Request("replay", new CreatorReplay("home"));
        Assert.Equal(0L, creator.App.State.ReplayCursor);
        Assert.Equal("Event 1 / 4", creator.App.State.Status);
        await creator.Request("replay", new CreatorReplay("arrowright"));
        Assert.Equal(1L, creator.App.State.ReplayCursor);
        await creator.Request("replay", new CreatorReplay("end"));
        Assert.Equal(3L, creator.App.State.ReplayCursor);
        Assert.Equal(2, Bumps(creator.App.Render()));

        // Letting go of the slider child at 2 scrubs to step 2 (value 2 -> cursor 1); Creator draws the value back as its prop.
        await Send(creator, CreatorView.ReplaySliderKey, new SetValue(2));
        Assert.Equal(1L, creator.App.State.ReplayCursor);
        Assert.Equal(2d, PropsOf<SliderProps>(creator.App.Render(), CreatorView.ReplaySliderKey).Value);

        // A value outside the timeline is refused by the slider's gate and moves nothing.
        await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(creator, CreatorView.ReplaySliderKey, new SetValue(9)));
        Assert.Equal(1L, creator.App.State.ReplayCursor);

        // The transport's toggle returns to Live mode (ReplayCursor == null)
        await creator.Request("replay", new CreatorReplay("toggle"));
        Assert.Null(creator.App.State.ReplayCursor);
        Assert.Equal("Live · Event 4 / 4", creator.App.State.Status);
    }

    [Fact]
    public async Task InspectorTabsSwitchThroughTheTabChildAndSelectingASignalRowScrubsTheTimeline()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        await os.Idle();

        // 1. The state tab shows the live unit JSON in a jsonTree node.
        Assert.Equal(5, Bumps(creator.App.Render()));
        Assert.Equal("Live State · ctr", JsonTreeOf(creator.App.Render()).Title);
        var tabs = PropsOf<TabStripProps>(creator.App.Render(), CreatorView.InspectorTabsKey);
        Assert.Equal(["state", "source", "signals", "state-graph", "scenarios"], tabs.Items.Select(i => i.Id));
        Assert.Equal("state", tabs.SelectedId);

        // 2. Switch to the source tab through the tab child -> a code block holds the C# unit source
        await Send(creator, CreatorView.InspectorTabsKey, new Select("source"));
        Assert.Equal("source", creator.App.State.InspectorTab);
        Assert.Equal("source", PropsOf<TabStripProps>(creator.App.Render(), CreatorView.InspectorTabsKey).SelectedId);
        var source = Assert.Single(AllNodes(creator.App.Render()).OfType<Surface>(), s => s.Kind == "code:csharp");
        Assert.Equal("Counter.cs", source.Title);
        Assert.Contains("public sealed record CounterState", source.Subtitle, StringComparison.Ordinal);

        // 3. Switch to the state graph tab -> mounts the Accordant BrainSceneNode
        await Send(creator, CreatorView.InspectorTabsKey, new Select("state-graph"));
        Assert.Equal("state-graph", creator.App.State.InspectorTab);
        Assert.True(AllNodes(creator.App.Render()).OfType<BrainSceneNode>().Count() >= 2);

        // An unknown tab is refused by the strip's gate.
        await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(creator, CreatorView.InspectorTabsKey, new Select("timeline")));
        Assert.Equal("state-graph", creator.App.State.InspectorTab);

        // 4. Open the trace and select a row of the signals table child (rows keyed by sequence) -> scrubs ReplayCursor to that signal
        await creator.Request("trace", new CreatorTrace("t-travel"));
        Assert.Equal("signals", creator.App.State.InspectorTab);
        var table = PropsOf<QueryTableProps>(creator.App.Render(), CreatorView.InspectorSignalsKey);
        Assert.Equal(["1", "2", "3", "4"], table.Rows!.Select(r => r.Id));
        Assert.Null(table.SelectedRowId);

        await Send(creator, CreatorView.InspectorSignalsKey, new SelectRow("2"));
        Assert.Equal(1L, creator.App.State.ReplayCursor);
        Assert.Equal("2", PropsOf<QueryTableProps>(creator.App.Render(), CreatorView.InspectorSignalsKey).SelectedRowId);
        await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(creator, CreatorView.InspectorSignalsKey, new SelectRow("99")));
        await Send(creator, CreatorView.InspectorTabsKey, new Select("state"));
        Assert.Equal(1, Bumps(creator.App.Render()));

        // 5. Back-to-back tab switches, then scenarios: each select is an absolute choice, so the last one wins.
        await Send(creator, CreatorView.InspectorTabsKey, new Select("source"));
        await Send(creator, CreatorView.InspectorTabsKey, new Select("signals"));
        await Send(creator, CreatorView.InspectorTabsKey, new Select("scenarios"));
        Assert.Equal("scenarios", creator.App.State.InspectorTab);
        Assert.Equal("scenarios", PropsOf<TabStripProps>(creator.App.Render(), CreatorView.InspectorTabsKey).SelectedId);

        creator.App.RunScenarios = (_, _, _, _) => Task.FromResult("✓ Counter increments by 1");
        await creator.Request("scenario", new CreatorScenario("ctr"));
        Assert.Equal("✓ Counter increments by 1", creator.App.State.Transcript);
    }

    // Owner decision 8: the inspection is stored state, so a Creator that is deactivated and activated again draws the same inspector
    // without reading the unit or the trace again.
    [Fact]
    public async Task AReactivatedCreatorDrawsItsInspectionFromStoredState()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        var unit = await Reactivated(os, creator.App);
        Assert.Equal(5, Bumps(unit.Render()));
        Assert.Equal("Live State · ctr", JsonTreeOf(unit.Render()).Title);
        Assert.Equal(["Counter.cs", "scenarios.json", "spec.md"], unit.State.Inspection.Files.Select(f => f.Path).Order(StringComparer.Ordinal));

        await creator.Request("trace", new CreatorTrace("t-travel"));
        await creator.Request("replay", new CreatorReplay("2"));
        var before = unit.Render();
        var traced = await Reactivated(os, unit);
        var after = traced.Render();

        Assert.Equal(2L, traced.State.ReplayCursor);
        Assert.Equal(4, traced.State.Inspection.Hops.Length);
        Assert.Equal(
            PropsOf<QueryTableProps>(before, CreatorView.InspectorSignalsKey).Rows!.Select(r => (r.Id, string.Join('|', r.Cells))),
            PropsOf<QueryTableProps>(after, CreatorView.InspectorSignalsKey).Rows!.Select(r => (r.Id, string.Join('|', r.Cells))));
        Assert.Equal("3", PropsOf<QueryTableProps>(after, CreatorView.InspectorSignalsKey).SelectedRowId);
        Assert.Equal(PropsOf<SliderProps>(before, CreatorView.ReplaySliderKey), PropsOf<SliderProps>(after, CreatorView.ReplaySliderKey));
    }

    [Fact]
    public void DeadOperationsAreRemovedFromCreatorManifestAndSignals()
    {
        var opNames = CreatorApp.Bundle.Manifest.Operations.Select(o => o.Name).ToArray();
        Assert.DoesNotContain("seed", opNames);
        Assert.DoesNotContain("make", opNames);
        Assert.DoesNotContain("change", opNames);

        var signalTypes = typeof(CreatorApp).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(CreatorApp).Namespace && typeof(Signal).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToArray();
        Assert.DoesNotContain("CreatorSeed", signalTypes);
        Assert.DoesNotContain("CreatorMake", signalTypes);
        Assert.DoesNotContain("CreatorChange", signalTypes);
    }

    private static TestOS CreateOs()
    {
        var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<TimeTravelFlows>();
        os.Register<TimeTravelApprovals>();
        os.Register<TimeTravelSynapses>();
        os.Register<TimeTravelReads>();
        os.Register<TimeTravelTraces>();
        return os;
    }

    // Sends a control command to Creator's child `key` as the person, through Creator's own gate, as the client's /act does.
    private static async Task Send(InstalledApp<CreatorApp> creator, string key, Signal command)
    {
        var alias = command.GetType().GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias;
        await creator.Screen.Dispatch(new Command(key, alias, JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));
        await creator.OS.Idle();
    }

    // The props Creator drew for its child `key`.
    private static T PropsOf<T>(View view, string key) where T : ControlProps
    {
        var child = Assert.Single(AllNodes(view).OfType<NeuronRef>(), n => n.Kind == "slot" && n.NeuronUri.EndsWith("/" + key, StringComparison.Ordinal));
        return child.Props!.Value.Deserialize<T>(JsonSerializerOptions.Web)!;
    }

    private static JsonTree JsonTreeOf(View view) => Assert.Single(AllNodes(view).OfType<JsonTree>());

    private static int Bumps(View view) => JsonDocument.Parse(JsonTreeOf(view).Json).RootElement.GetProperty("bumps").GetInt32();

    // Deactivates Creator and activates it again: the new instance has only what Creator stored.
    private static async Task<CreatorApp> Reactivated(TestOS os, CreatorApp before)
    {
        var id = new NeuronId(new BrainId("local"), "app.creator", "default");
        await os.Deactivate(id);
        CreatorApp? again = null;
        using (IntoChat.OS.Host.Apps.AppActivationScope.Capture(neuron => again ??= neuron as CreatorApp))
        {
            await os.Activate(id);
        }
        await os.Idle();
        Assert.NotNull(again);
        Assert.NotSame(before, again);
        return again;
    }

    private static IEnumerable<Node> AllNodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => AllNodes(p.GetValue(node)))],
        View view => AllNodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(AllNodes),
        _ => [],
    };

    [Neuron("flows")]
    private sealed class TimeTravelFlows : Neuron, IFlows
    {
        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult<FlowView[]>([]);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult<FlowView?>(null);
        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
            => Task.FromResult("local/flow/1");
        public Task Resume(string flowId, string step, DigitalBrain.Flows.WaitOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task Cancel(string flowId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task Relate(string flowId, DigitalBrain.Flows.FlowRelation relation, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Neuron("approvals")]
    private sealed class TimeTravelApprovals : Neuron, IApprovals
    {
        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Approval>>([]);
    }

    [Neuron("ino.synapses")]
    private sealed class TimeTravelSynapses : Neuron, ISynapseStudio
    {
        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult(""));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => Task.FromResult(new UnitDeployResult(true, "", DeployStatus.Deployed, null, new Dictionary<string, InstallOutcome>()));
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spec.md"] = "# Counter Spec",
            ["scenarios.json"] = """[{"name":"bumps","steps":[]}]""",
        });
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Counter.cs"] = """
                [State("ctr")]
                public sealed record CounterState(int Bumps = 0);
                """,
        });
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("", Ok: true));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
        public Task<DeployedSource[]> List() => Task.FromResult<DeployedSource[]>([]);
    }

    [Neuron("creator.reads")]
    private sealed class TimeTravelReads : Neuron, IDebuggerReads
    {
        public Task<string?> State(string alias) => Task.FromResult<string?>("""{"bumps":5,"mode":"live"}""");
        public Task<string?> View(string alias) => Task.FromResult<string?>("""{"title":"Counter"}""");
        public Task<TracePage> Signals(string alias, string? publisher, int limit) => Task.FromResult(new TracePage([]));
        public Task<TracePage> Failures(string alias, int limit) => Task.FromResult(new TracePage([]));
        public Task<TraceEntry?> Failing(string alias) => Task.FromResult<TraceEntry?>(null);
        public Task<BrainTopology> Topology() => Task.FromResult(new BrainTopology(
            [new(Unit, "ctr", BrainGraph.Unit)],
            [new("*", Unit, "ctr.bump", "reaction")]));
        public Task<bool> Loaded(string alias) => Task.FromResult(true);
        public Task<long> Head() => Task.FromResult(10L);
    }

    [Neuron("trace.reader")]
    private sealed class TimeTravelTraces : Neuron, ITraceReader
    {
        private static readonly TraceEntry[] SampleEntries =
        [
            new("local/ino/ino", 1, "ctr.bump", T0, "t-travel", null, """{"delta":1}"""),
            new(Unit, 2, ReplayScript.StateChangedAlias, T0.AddMilliseconds(12), "t-travel", "local/ino/ino:1", """{"bumps":1,"mode":"active"}"""),
            new("local/ino/ino", 3, "ctr.bump", T0.AddMilliseconds(25), "t-travel", $"{Unit}:2", """{"delta":1}"""),
            new(Unit, 4, ReplayScript.StateChangedAlias, T0.AddMilliseconds(37), "t-travel", "local/ino/ino:3", """{"bumps":2,"mode":"active"}"""),
        ];

        public Task<TracePage> ReadTrace(string traceId, long since, int limit)
            => Task.FromResult(new TracePage([.. SampleEntries.Where(e => e.TraceId == traceId && e.Sequence > since)]));
    }
}
