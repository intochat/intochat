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
    public void CreatorState_AggregateExplorer_UnfoldsHealthyStateSpaceAndRuleRefusals()
    {
        var graph = AggregateExplorer<CreatorState>.Explore(
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
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("empty_draft"));

        var scene = graph.ToScene();
        Assert.NotEmpty(scene.Nodes);
        Assert.NotEmpty(scene.Synapses);
    }

    [Fact]
    public async Task CreatorApp_TogglesAccordantStateGraphAndInspectsUnitStateSpace()
    {
        await using var os = TestOS.Local();
        os.Register<LocalCreatorReads>();
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen());
        Assert.False(creator.App.State.ShowStateGraph);

        // Toggle to Accordant State Graph via CreatorSelect("view:state-graph")
        var toggleBtn = Assert.Single(AllNodes(creator.App.Render()).OfType<Button>(), b => b.Key == "toggle-state-graph");
        Assert.NotNull(toggleBtn.Emit);
        await creator.Request("select", new CreatorSelect(CreatorView.ToggleStateGraphTarget));
        Assert.True(creator.App.State.ShowStateGraph);

        var stateScene = Assert.Single(AllNodes(creator.App.Render()).OfType<BrainSceneNode>());
        Assert.True(stateScene.PauseRotation);
        Assert.NotEmpty(stateScene.Nodes);

        // Toggle back to Live Brain Graph
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
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var graph = AggregateExplorer<TasksState>.Explore(
            maxStates: 20,
            render: s => TasksViewRenderer.Render(s, [], [], t0).Root);

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.True(graph.States.Count >= 3);
        Assert.Contains(graph.States, s => s.State.View == "board");
        Assert.Contains(graph.States, s => s.State.View == "list");
        Assert.Contains(graph.States, s => s.State.View == "timeline");
    }

    [Fact]
    public void DesktopState_AggregateExplorer_EnforcesDomainRulesAndRendersCleanly()
    {
        var desktopState = new DesktopAppState();
        Assert.Equal("intochat", desktopState.StylePreset);
        Assert.True(desktopState.WindowCapHolds);
        Assert.True(desktopState.TabCapHolds);
        Assert.True(desktopState.DesktopNeverPinned);

        // Pinning "ino" or "desktop" is refused by domain rule
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
