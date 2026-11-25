using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed record IntrospectDemoState
{
    public string Status { get; init; } = "idle";
}

public sealed record IntrospectPanelState
{
    public string Draft { get; init; } = "";
}

/*
# Introspect demo

A test app whose screen draws a button and mounts a panel child under "panel", so the introspector must read the child through the screen.
*/
[Neuron("app.introspectdemo")]
public sealed class IntrospectDemoApp : Screen<IntrospectDemoState>
{
    protected override View Render(IntrospectDemoState state)
        => new("Introspect demo", new Column([
            new Text($"Status {state.Status}"),
            new Button("refresh", "Refresh", new Command(".", "introspectdemo.refresh", null)),
            Child<IntrospectPanel>("panel"),
        ]));
}

[Neuron("test.introspect-panel")]
public sealed class IntrospectPanel : Screen<IntrospectPanelState>
{
    protected override View Render(IntrospectPanelState state)
        => new("Panel", new Column([
            new Text("PANEL_CONTENT_MARKER"),
            new Button("save", "Save draft", new Command(".", "introspect-panel.save", null)),
        ]));
}

// The introspector sees what a screen draws, its children included, by reading it through the screen directory.
public sealed class UiIntrospectorFacts
{
    private static readonly BrainId TestBrain = new("ui-introspect");

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    private static TestOS ScreensOs()
    {
        var os = TestOS.Local();
        os.Register(typeof(FlutterModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();
        os.Register<IntrospectDemoApp>().Register<IntrospectPanel>();
        return os;
    }

    public sealed record SampleCounterAggregate(int Count = 0, bool Locked = false)
    {
        public SampleCounterAggregate() : this(0, false) { }

        [Invariant("Count is non-negative.")]
        public bool NonNegativeCount => Count >= 0;

        public Decision<SampleCounterAggregate> Increment() =>
            Decision.Check("increment", Decision.Rule("not_locked", this, static s => s.Locked, "Counter is locked."))
            ?? Decision.Accept(this with { Count = Math.Min(2, Count + 1) });

        public SampleCounterAggregate ToggleLock() => this with { Locked = !Locked };
    }

    [Fact]
    public async Task A_screen_is_read_through_the_directory_with_its_child_under_the_child_key()
    {
        using var person = AsPerson();
        await using var os = ScreensOs();
        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        var screen = await introspector.InspectScreen("introspectdemo");

        Assert.NotNull(screen);
        Assert.Equal("introspectdemo", screen.AppId);
        Assert.True(screen.Generation > 0);
        Assert.Contains(screen.Nodes, node => node is { Key: "refresh", Kind: "button", Label: "Refresh" });
        // The child is drawn in place: its nodes carry the child key path, its content is in the view.
        Assert.Contains(screen.Nodes, node => node is { Key: "panel/save", Kind: "button", Label: "Save draft" });
        Assert.Contains("PANEL_CONTENT_MARKER", screen.ViewJson, StringComparison.Ordinal);
        Assert.Contains("Status idle", screen.ViewJson, StringComparison.Ordinal);
        Assert.Contains("panel/save", screen.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_screen_reads_as_null()
    {
        using var person = AsPerson();
        await using var os = ScreensOs();
        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        Assert.Null(await introspector.InspectScreen("no-such-app"));
    }

    [Fact]
    public async Task The_open_screens_and_explored_state_graphs_are_projected_into_the_agent_context()
    {
        using var person = AsPerson();
        await using var os = ScreensOs();
        var workspace = os.Get<IWorkspace>(new NeuronId(TestBrain, IWorkspace.Type, "main"));
        await workspace.Show($"{TestBrain.Value}/apps/introspectdemo", "op-open-1", 0);
        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        var open = await introspector.InspectOpenScreens();

        var screen = Assert.Single(open);
        Assert.Equal("introspectdemo", screen.AppId);
        Assert.Contains(screen.Nodes, node => node.Key == "panel/save");

        var graph = await introspector.ExploreStateGraph(typeof(SampleCounterAggregate).FullName!, maxStates: 16);
        Assert.True(graph.IsHealthy);
        Assert.True(graph.States.Length >= 4);
        Assert.Contains(graph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("not_locked"));
        Assert.Contains("digraph SampleCounterAggregate", graph.Dot, StringComparison.Ordinal);

        var report = await introspector.SyncContext();
        Assert.Equal(["introspectdemo"], report.Screens.Select(s => s.AppId));
        Assert.Contains("panel/save", report.ContextSummary, StringComparison.Ordinal);
        Assert.Contains("SampleCounterAggregate", report.ContextSummary, StringComparison.Ordinal);

        var uiStateContext = await os.Get<IAgentContext>(new NeuronId(TestBrain, "agent-context", IUiIntrospector.DefaultContextKey)).Read();
        var entry = Assert.Single(uiStateContext.Entries, e => e.Name == "ui.introspector");
        Assert.Contains("Open screens", entry.Text, StringComparison.Ordinal);
        Assert.Contains("introspectdemo", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Accordant StateGraphs:", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_screen_directory_the_introspector_sees_no_screens()
    {
        using var person = AsPerson();
        await using var os = TestOS.Local();
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();
        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        Assert.Empty(await introspector.InspectOpenScreens());
        Assert.Null(await introspector.InspectScreen("introspectdemo"));
        var report = await introspector.SyncContext();
        Assert.Empty(report.Screens);
    }

    [Fact]
    public async Task State_stored_by_the_kind_and_key_introspector_loads_and_keeps_its_state_graphs()
    {
        using var person = AsPerson();
        await using var os = ScreensOs();
        var id = IUiIntrospector.NeuronIdOf(TestBrain);
        // The shape the ui.* introspector saved: tracked kind+key references beside the explored graphs.
        os.SeedStoredState(id, """
            {"revision":7,"trackedNeurons":[{"neuronKind":"ui.tabs","key":"workspace/tabs"}],
             "stateGraphs":[{"name":"Stored","typeName":"Sample.Stored","isHealthy":true,"truncated":false,"states":[],"transitions":[],"violations":[],"dot":"digraph Stored {}"}]}
            """);
        var introspector = os.Get<IUiIntrospector>(id);

        var graph = Assert.Single(await introspector.ListStateGraphs());

        Assert.Equal(("Stored", "digraph Stored {}"), (graph.Name, graph.Dot));
        var report = await introspector.SyncContext();
        Assert.Equal(8, report.Revision);
    }

    [Fact]
    public void The_AI_module_no_longer_references_the_ui_control_module()
        => Assert.DoesNotContain(
            typeof(UiIntrospectorNeuron).Assembly.GetReferencedAssemblies(),
            name => name.Name == "IntoChat.OS.Modules.UI");
}
