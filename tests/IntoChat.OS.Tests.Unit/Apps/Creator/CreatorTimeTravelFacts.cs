using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

[Collection(nameof(CreatorTimeTravelFacts))]
public sealed class CreatorTimeTravelFacts
{
    private const string Unit = "local/ctr/default";
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TimeTravelTransportAndKeyboardBindingsStepOneEventAtATimeAndSyncPropertyGrid()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen("trace/t-travel"));
        await os.Idle();

        var propGrid = os.Get<IPropertyGrid>(new NeuronId(new BrainId("local"), "ui.property-grid", CreatorView.InspectorStateKey));
        var slider = os.Get<ISlider>(new NeuronId(new BrainId("local"), "ui.slider", CreatorView.ReplaySliderKey));
        var transport = os.Get<ICommandBar>(new NeuronId(new BrainId("local"), "ui.command-bar", CreatorView.ReplayCommandBarKey));

        // Initially live at latest state (bumps: 2)
        Assert.Null(creator.App.State.ReplayCursor);
        Assert.Equal(4, creator.App.State.ReplayStepCount);
        var liveGrid = await propGrid.Read();
        Assert.Contains(liveGrid.Entries, e => e.Name == "bumps" && e.Value == "2");

        // Space -> pause / toggle into Replay at latest event (index 3 -> Event 4 / 4)
        await creator.Request("replay", new CreatorReplay("space"));
        await os.Idle();
        Assert.Equal(3L, creator.App.State.ReplayCursor);
        Assert.Equal(4d, (await slider.Read()).Value);

        // ArrowLeft -> step backward 1 signal to index 2, then ArrowLeft -> index 1 (StateChanged: bumps = 1)
        await creator.Request("replay", new CreatorReplay("arrowleft"));
        await creator.Request("replay", new CreatorReplay("arrowleft"));
        await os.Idle();
        Assert.Equal(1L, creator.App.State.ReplayCursor);
        Assert.Equal("Event 2 / 4", creator.App.State.Status);
        Assert.Equal(2d, (await slider.Read()).Value);

        var step2Grid = await propGrid.Read();
        Assert.Contains(step2Grid.Entries, e => e.Name == "bumps" && e.Value == "1");

        // Home -> jump to first event (index 0 -> Event 1 / 4)
        await creator.Request("replay", new CreatorReplay("home"));
        await os.Idle();
        Assert.Equal(0L, creator.App.State.ReplayCursor);
        Assert.Equal("Event 1 / 4", creator.App.State.Status);

        // ArrowRight -> step forward 1 signal (index 1 -> Event 2 / 4)
        await creator.Request("replay", new CreatorReplay("arrowright"));
        await os.Idle();
        Assert.Equal(1L, creator.App.State.ReplayCursor);

        // End -> jump to latest event (index 3 -> Event 4 / 4, bumps = 2)
        await creator.Request("replay", new CreatorReplay("end"));
        await os.Idle();
        Assert.Equal(3L, creator.App.State.ReplayCursor);
        var step4Grid = await propGrid.Read();
        Assert.Contains(step4Grid.Entries, e => e.Name == "bumps" && e.Value == "2");

        // Dragging ISlider scrubs directly to step 2 (value = 2 -> cursor = 1)
        await slider.SetValue(2);
        await os.Idle();
        Assert.Equal(1L, creator.App.State.ReplayCursor);

        // Clicking ICommandBar "toggle" returns to Live mode (ReplayCursor == null)
        await transport.Invoke("toggle");
        await os.Idle();
        Assert.Null(creator.App.State.ReplayCursor);
        Assert.Equal("Live · Event 4 / 4", creator.App.State.Status);
    }

    [Fact]
    public async Task InspectorTabsSwitchCleanlyAndSelectingASignalRowScrubsTimeline()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        await os.Idle();

        var tabs = os.Get<ITabs>(new NeuronId(new BrainId("local"), "ui.tabs", CreatorView.InspectorTabsKey));
        var propGrid = os.Get<IPropertyGrid>(new NeuronId(new BrainId("local"), "ui.property-grid", CreatorView.InspectorStateKey));
        var codeEditor = os.Get<ICodeEditor>(new NeuronId(new BrainId("local"), "ui.code-editor", CreatorView.InspectorSourceKey));
        var signalsTable = os.Get<ITable>(new NeuronId(new BrainId("local"), "ui.table", CreatorView.InspectorSignalsKey));

        // 1. State tab shows live unit JSON in IPropertyGrid
        var gridState = await propGrid.Read();
        Assert.Contains(gridState.Entries, e => e.Name == "bumps" && e.Value == "5");

        // 2. Switch to Source tab via ITabs -> ICodeEditor holds C# unit source
        await tabs.Select("source");
        await os.Idle();
        Assert.Equal("source", creator.App.State.InspectorTab);
        var editorState = await codeEditor.Read();
        Assert.Equal("Counter.cs", editorState.Title);
        Assert.Equal("csharp", editorState.Language);
        Assert.Contains("public sealed record CounterState", editorState.Content, StringComparison.Ordinal);

        // 3. Switch to State Graph tab -> mounts Accordant BrainSceneNode
        await tabs.Select("state-graph");
        await os.Idle();
        Assert.Equal("state-graph", creator.App.State.InspectorTab);
        var stateGraphNodes = AllNodes(creator.App.Render()).OfType<BrainSceneNode>().ToArray();
        Assert.True(stateGraphNodes.Length >= 2);

        // 4. Open trace and select a row in ITable -> scrubs ReplayCursor to that signal
        await creator.Request("trace", new CreatorTrace("t-travel"));
        await os.Idle();
        Assert.Equal("signals", creator.App.State.InspectorTab);

        await signalsTable.SelectRow(1);
        await os.Idle();
        Assert.Equal(1L, creator.App.State.ReplayCursor);
        Assert.Contains((await propGrid.Read()).Entries, e => e.Name == "bumps" && e.Value == "1");

        // 5. Switch to Scenarios tab and run scenario (including back-to-back sequential ITabs switches without Idle in between)
        await tabs.Select("source");
        await tabs.Select("signals");
        await tabs.Select("state-graph");
        await tabs.Select("scenarios");
        await os.Idle();
        Assert.Equal("scenarios", creator.App.State.InspectorTab);
        Assert.Equal("scenarios", (await tabs.Read()).SelectedId);

        creator.App.RunScenarios = (_, _, _, _) => Task.FromResult("✓ Counter increments by 1");
        await creator.Request("scenario", new CreatorScenario("ctr"));
        await os.Idle();
        Assert.Equal("✓ Counter increments by 1", creator.App.State.Transcript);
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
