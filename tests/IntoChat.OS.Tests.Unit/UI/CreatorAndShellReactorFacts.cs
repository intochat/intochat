using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Synapses;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

public sealed class CreatorAndShellReactorFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly SynapseCompiler HostCompiler = new(ReferenceSet.FromDefaultContext());

    [Fact]
    public void CreatorWorkspaceState_AggregateExplorer_UnfoldsHealthyStateSpaceAndRuleRefusals()
    {
        var graph = AggregateExplorer<CreatorWorkspaceState>.Explore(
            maxStates: 24,
            render: s => CreatorView.Render(
                s,
                new BrainTopology([new("local/ctr/default", "ctr", BrainGraph.Unit)], []),
                [],
                [],
                Inspection.None).Root);

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.True(graph.States.Count >= 4);
        Assert.NotEmpty(graph.AcceptedTransitions);
        Assert.NotEmpty(graph.RejectedTransitions);
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_inspector_tab") || t.BrokenRules.Contains("empty_timeline"));

        var scene = graph.ToScene();
        Assert.NotEmpty(scene.Nodes);
        Assert.NotEmpty(scene.Synapses);
    }

    [Fact]
    public async Task CreatorApp_TogglesAccordantStateGraphAndInspectsUnitStateSpace()
    {
        await using var os = TestOS.Local();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        os.Register<LocalCreatorReads>();
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen());
        Assert.False(creator.App.State.ShowStateGraph);

        // The graph mode is a control.tabStrip child: a select sent through Creator switches the mode and is drawn back as its prop.
        var switcher = Assert.Single(AllNodes(creator.App.Render()).OfType<NeuronRef>(), r => r.NeuronUri.EndsWith("/" + CreatorView.GraphModeKey, StringComparison.Ordinal));
        Assert.Equal("brain", switcher.Props!.Value.GetProperty("selectedId").GetString());
        Assert.Contains(switcher.Props!.Value.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetString() == "state");
        await creator.Screen.Dispatch(new Command(CreatorView.GraphModeKey, "control.select", JsonSerializer.SerializeToElement(new { id = "state" })));
        await os.Idle();
        Assert.True(creator.App.State.ShowStateGraph);
        switcher = Assert.Single(AllNodes(creator.App.Render()).OfType<NeuronRef>(), r => r.NeuronUri.EndsWith("/" + CreatorView.GraphModeKey, StringComparison.Ordinal));
        Assert.Equal("state", switcher.Props!.Value.GetProperty("selectedId").GetString());

        var stateScene = Assert.Single(AllNodes(creator.App.Render()).OfType<BrainSceneNode>());
        Assert.True(stateScene.PauseRotation);
        Assert.NotEmpty(stateScene.Nodes);

        await creator.Request("select", new CreatorSelect(CreatorView.ToggleStateGraphTarget));
        Assert.False(creator.App.State.ShowStateGraph);
    }

    [Fact]
    public void CreatorStateSpaceInspector_ExtractsStateTransitionsAndRulesFromUnitSource()
    {
        var unitJson = JsonSerializer.Serialize(OfflineUnitSynthesizer.SynthesizeAuthoredUnit("Create a Hello World toggle app"));
        var files = OfflineUnitSynthesizer.SynthesizeUnitFiles(unitJson);

        var summary = CreatorStateSpaceInspector.AnalyzeUnitSource("hello.world", files);
        Assert.Equal("HelloWorldState", summary.StateType);
        Assert.Contains(summary.Commands, c => c.Contains("Toggle", StringComparison.Ordinal));
        Assert.Contains("hello.toggled", summary.Events);

        var card = CreatorStateSpaceInspector.RenderUnitSummaryCard("hello.world", files);
        Assert.Equal("Aggregate State Graph", card.Title);

        var unitScene = CreatorStateSpaceInspector.RenderUnitStateScene("hello.world", files);
        Assert.True(unitScene.PauseRotation);
        Assert.True(unitScene.Nodes.Count >= 2);
    }

    [Fact]
    public void TasksState_AggregateExplorer_VerifiesViewModeInvariantsAndKitRenderer()
    {
        var graph = AggregateExplorer<TasksWorkspaceState>.ExploreScreen<TasksApp>(maxStates: 24).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.True(graph.States.Count >= 3);
        Assert.Contains(graph.States, s => s.State.ActiveTab == "board");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "list");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "details");
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("empty_title", StringComparer.Ordinal));
    }

    [Fact]
    public void DesktopState_AggregateExplorer_EnforcesDomainRulesAndRendersCleanly()
    {
        var desktopState = new DesktopAppState();
        Assert.Equal("intochat", desktopState.StylePreset);
        Assert.True(desktopState.WindowCapHolds);
        Assert.True(desktopState.TabCapHolds);
        Assert.True(desktopState.DesktopNeverPinned);

        var refusedIno = desktopState.PinOrUnpin("ino", pinned: false);
        Assert.True(refusedIno.IsRefused);
        Assert.Contains("core_dock_app", refusedIno.Rejection!.Rules);

        var acceptedPin = desktopState.PinOrUnpin("photocraft", pinned: true);
        Assert.True(acceptedPin.IsAccepted);
        Assert.Contains("photocraft", acceptedPin.Next!.PinnedApps);

        var desktopGraph = AggregateExplorer<DesktopAppState>.Explore(maxStates: 16);
        Assert.True(desktopGraph.IsHealthy, string.Join("\n", desktopGraph.Violations));
        Assert.Contains(desktopGraph.RejectedTransitions, t => t.BrokenRules.Contains("core_dock_app"));
    }

    [Theory]
    [InlineData("Create a Hello World greeting app", "hello.world")]
    [InlineData("Build a budget expense tracker", "budget.app")]
    [InlineData("Interactive todo list tracker", "todo.app")]
    [InlineData("Reactive workflow watch every 5m", "flow.watch")]
    public async Task OfflineUnitSynthesizer_EmitsDesignTokenTagPillsAndPassesCompilerScenarios(string prompt, string expectedAlias)
    {
        var authored = OfflineUnitSynthesizer.SynthesizeAuthoredUnit(prompt);
        Assert.Equal(expectedAlias, authored.Alias);

        var files = OfflineUnitSynthesizer.SynthesizeUnitFiles(JsonSerializer.Serialize(authored));
        var cs = files["UnitApp.cs"];
        Assert.Contains("new TagPill(", cs, StringComparison.Ordinal);
        Assert.Contains("new Surface(", cs, StringComparison.Ordinal);

        var check = await BuilderTools.Check(HostCompiler, authored.Alias, files, authored.Needs, authored.Scenarios, Ct);
        Assert.True(
            check.Passed,
            string.Join("\n", check.Diagnostics.Select(d => $"{d.Id}: {d.Message}").Concat(check.Failures)));
    }

    private static IEnumerable<Node> AllNodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => AllNodes(p.GetValue(node)))],
        View view => AllNodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(AllNodes),
        _ => [],
    };

    [DigitalBrain.Neurons.Neuron("creator.reads")]
    private sealed class LocalCreatorReads : DigitalBrain.Neurons.Neuron, IDebuggerReads
    {
        public Task<string?> State(string alias) => Task.FromResult<string?>("{}");
        public Task<string?> View(string alias) => Task.FromResult<string?>("{}");
        public Task<IntoChat.OS.Modules.Apps.Trace.TracePage> Signals(string alias, string? publisher, int limit)
            => Task.FromResult(new IntoChat.OS.Modules.Apps.Trace.TracePage([]));
        public Task<IntoChat.OS.Modules.Apps.Trace.TracePage> Failures(string alias, int limit)
            => Task.FromResult(new IntoChat.OS.Modules.Apps.Trace.TracePage([]));
        public Task<IntoChat.OS.Modules.Apps.Trace.TraceEntry?> Failing(string alias)
            => Task.FromResult<IntoChat.OS.Modules.Apps.Trace.TraceEntry?>(null);
        public Task<BrainTopology> Topology()
            => Task.FromResult(new BrainTopology([new("local/ctr/default", "ctr", BrainGraph.Unit)], []));
        public Task<bool> Loaded(string alias) => Task.FromResult(true);
        public Task<long> Head() => Task.FromResult(1L);
    }
}
