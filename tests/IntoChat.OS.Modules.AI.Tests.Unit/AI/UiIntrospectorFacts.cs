using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class UiIntrospectorFacts
{
    private static readonly BrainId TestBrain = new("ui-introspect");

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

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
    public async Task DiscoversModulesUiContractsInspectsLiveUiNeuronsAndProjectsStateGraphIntoAgentContext()
    {
        using var person = AsPerson();
        var observerBridge = new DelegatingBrainObserver();
        var services = new ServiceCollection();
        services.AddSingleton<IBrainObserver>(observerBridge);

        await using var os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        observerBridge.Inner = os;
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        // 1. Configure live Modules.UI neurons in the brain.
        var tabs = os.Get<ITabs>(new NeuronId(TestBrain, "ui.tabs", "inspector/tabs"));
        await tabs.Configure(
            [new UiTabItem("plan", "Plan"), new UiTabItem("code", "Code"), new UiTabItem("verify", "Verify")],
            selectedId: "code");

        var chat = os.Get<IChatThread>(new NeuronId(TestBrain, "ui.chat", "engineer/thread"));
        await chat.Configure("conv-eng", "Engineer Brainstorm", "gpt-5.4", [new ChatModelOption("gpt-5.4", "GPT-5.4")]);
        await chat.AppendMessage(new ChatMessage("m1", "user", "Design isolated worktree lease"));

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        // 2. Contract discovery surfaces Modules.UI component contracts, state types, methods, and signals.
        var contracts = await introspector.Contracts();
        Assert.Contains(contracts, c => c.NeuronKind == "ui.tabs" && c.StateType == nameof(TabsState) && c.Signals.Contains("ui.tab-changed"));
        Assert.Contains(contracts, c => c.NeuronKind == "ui.chat" && c.StateType == nameof(UiChatThreadState));

        // 3. Direct and live brain-wide neuron inspection return current state JSON and summary.
        var singleTabs = await introspector.InspectNeuron("ui.tabs", "inspector/tabs");
        Assert.NotNull(singleTabs);
        Assert.Equal($"{TestBrain.Value}/inspector/tabs", singleTabs.NeuronKey);
        Assert.Contains("\"selectedId\":\"code\"", singleTabs.StateJson, StringComparison.OrdinalIgnoreCase);

        var live = await introspector.InspectLiveNeurons();
        Assert.Contains(live, n => n.NeuronKind == "ui.tabs" && n.Key == "inspector/tabs");
        Assert.Contains(live, n => n.NeuronKind == "ui.chat" && n.Key == "engineer/thread");

        // 4. Explore Accordant StateGraph via AggregateExplorer and capture states, guarded transitions, and DOT.
        var graph = await introspector.ExploreStateGraph(typeof(SampleCounterAggregate).FullName!, maxStates: 16);
        Assert.True(graph.IsHealthy);
        Assert.True(graph.States.Length >= 4);
        Assert.Contains(graph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("not_locked"));
        Assert.Contains("digraph SampleCounterAggregate", graph.Dot, StringComparison.Ordinal);

        // 5. SyncContext writes compact live UI + StateGraph awareness into context/ui.state.
        var report = await introspector.SyncContext();
        Assert.Contains("inspector/tabs", report.ContextSummary, StringComparison.Ordinal);
        Assert.Contains("SampleCounterAggregate", report.ContextSummary, StringComparison.Ordinal);

        var uiStateContext = await os.Get<IAgentContext>(new NeuronId(TestBrain, "agent-context", IUiIntrospector.DefaultContextKey)).Read();
        var entry = Assert.Single(uiStateContext.Entries, e => e.Name == "ui.introspector");
        Assert.Contains("Active UI neurons:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Accordant StateGraphs:", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrackNeuronWorksWithoutBrainObserverAndRejectsUnknownUiKind()
    {
        using var person = AsPerson();
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var tabs = os.Get<ITabs>(new NeuronId(TestBrain, "ui.tabs", "creator/right-pane"));
        await tabs.Configure([new UiTabItem("state", "State"), new UiTabItem("signals", "Signals")], selectedId: "signals");

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));
        await introspector.TrackNeuron("ui.tabs", "creator/right-pane");

        var live = await introspector.InspectLiveNeurons();
        var snap = Assert.Single(live);
        Assert.Equal("creator/right-pane", snap.Key);
        Assert.Contains("signals", snap.StateJson, StringComparison.OrdinalIgnoreCase);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => introspector.TrackNeuron("not.a.ui.neuron", "x"));
        Assert.Contains("known_ui_kind", ex.Rejected.Rules);
    }

    private sealed class DelegatingBrainObserver : IBrainObserver
    {
        public IBrainObserver? Inner { get; set; }

        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
            => Inner!.Instances<T>(cancellationToken);

        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
            => Inner!.Instances(type, cancellationToken);

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
            => Inner!.Types(cancellationToken);

        public Task<string[]> Brains(CancellationToken cancellationToken = default)
            => Inner!.Brains(cancellationToken);

        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default)
            => Inner!.Wires(cancellationToken);

        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
            => Inner!.Deliveries(cancellationToken);

        public Task<System.Text.Json.JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
            => Inner!.State(id, cancellationToken);
    }
}
