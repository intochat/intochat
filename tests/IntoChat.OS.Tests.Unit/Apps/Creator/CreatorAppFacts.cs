using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Kernel;
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
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

[Collection(nameof(CreatorAppFacts))]
public sealed class CreatorAppFacts
{
    private const string Unit = "local/ctr/default";
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> CreatorOperations => ScreenBaseline.Cases(typeof(CreatorApp), Register);

    [Theory, MemberData(nameof(CreatorOperations))]
    public Task CreatorBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task SelectingAUnitPopulatesPropertyGridAndCodeEditorWithoutCardWall()
    {
        await using var os = FakeOs();
        os.Register(typeof(UiModule).Assembly);
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        FakeReads.States["ctr"] = """{"bumps":3}""";
        FakeReads.Views["ctr"] = """{"alias":"ctr","generation":2,"view":{"title":"Counter"}}""";
        FakeSynapses.Files = new() { ["ctr.cs"] = "// the counter" };
        FakeSynapses.SidecarFiles = new() { ["spec.md"] = "Counts bumps.", ["scenarios.json"] = "[]" };
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        await os.Idle();

        var view = creator.App.Render();
        var shell = Assert.IsType<AppShellNode>(view.Root);
        Assert.Equal("creator-shell", shell.Key);
        Assert.IsType<BrainSceneNode>(shell.Body);

        var propGrid = await os.Get<IPropertyGrid>(new NeuronId(new BrainId("local"), "ui.property-grid", CreatorView.InspectorStateKey)).Read();
        Assert.Contains(propGrid.Entries, e => e.Name == "bumps" && e.Value == "3");

        var codeEditor = await os.Get<ICodeEditor>(new NeuronId(new BrainId("local"), "ui.code-editor", CreatorView.InspectorSourceKey)).Read();
        Assert.Equal("// the counter", codeEditor.Content);
        Assert.Equal(new Selection(Selection.Node, Unit), creator.App.State.Selected);
    }

    [Fact]
    public async Task CreatorImplementsNavigatedToAndRuleGuardedNavigatedFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = FakeOs();
        FakeTraces.Entries = [Entry("local/ino/ino", 1, null)];
        var creator = await os.Install<CreatorApp>();

        await creator.App.NavigateToAsync(
            new StudioNavArgs(TargetId: "trace/t1", Mode: "state"),
            "app.engineer",
            ViewTransition.CameraFlyTo,
            null,
            ct);

        Assert.Equal("t1", creator.App.State.TraceId);
        Assert.Equal("state", creator.App.State.CanvasMode);
        var shell = Assert.IsType<AppShellNode>(creator.App.Render().Root);
        Assert.Equal(ViewTransition.CameraFlyTo, shell.Transition);

        // Guarded departure when a scenario verification run is in flight
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        creator.App.RunScenarios = (_, _, _, _) => release.Task;
        var inFlightTask = creator.App.Scenario(new CreatorScenario("ctr"));

        var blockedDeparture = await creator.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer"), ct);
        Assert.False(blockedDeparture);
        Assert.NotNull(creator.App.LastRejection);

        var forcedDeparture = await creator.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer", Force: true), ct);
        Assert.True(forcedDeparture);

        release.SetResult("✓ ok");
        await inFlightTask;
        Assert.True(await creator.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer"), ct));
    }

    [Fact]
    public async Task SelectingAPulseShowsTheTraceTreeAndTimeTravelBar()
    {
        await using var os = FakeOs();
        FakeTraces.Entries =
        [
            Entry("local/ino/ino", 1, null),
            Entry("local/flows/flows/index", 2, "local/ino/ino:1"),
            Entry("local/agent/agent", 3, "local/flows/flows/index:2"),
            Entry("local/ino/ino", 4, "local/ino/ino:1"),
        ];
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(Value: BrainGraph.PulseKey("t1")));

        var table = Assert.Single(Nodes(creator.App.Render()).OfType<DataTable>(), t => t.Title == "Trace t1");
        Assert.Equal(["1", "2", "3", "4"], table.Rows.Select(r => r[0]));
        Assert.Equal([0, 2, 4, 2], table.Rows.Select(r => r[2].Length - r[2].TrimStart().Length));
        Assert.Equal("signals", creator.App.State.InspectorTab);
        Assert.Equal("t1", creator.App.State.TraceId);

        var cursorReadout = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "replay-cursor-readout");
        Assert.Equal("Live · Event 4 / 4", cursorReadout.Label);

        var select = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == BrainGraph.SelectKey);
        Assert.Equal("select", select.Command!.Signal);
    }

    [Fact]
    public async Task TheDraftAppearsAsAGhostAfterAuthor()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Build("weather", ["author"]), Build("early", [])];
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen());

        var scene = Assert.Single(Nodes(creator.App.Render()).OfType<BrainSceneNode>());
        var ghost = Assert.Single(scene.Nodes, n => n.Kind == BrainGraph.Draft);
        Assert.Equal(BrainGraph.DraftId("weather"), ghost.Id);
        Assert.Contains(scene.Nodes, n => n.Id == Unit && n.Kind == BrainGraph.Unit);
        Assert.Equal("select", scene.Nodes.Single(n => n.Id == Unit).Select!.Command!.Signal);
    }

    [Fact]
    public async Task SelectingAnEdgeShowsItsRecentSignals()
    {
        await using var os = FakeOs();
        FakeReads.Recent = [Entry("local/kicker/a", 7, null, "ctr.bump"), Entry("local/kicker/a", 9, null, "ctr.bump")];
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(Value: BrainGraph.EdgeKey("*", Unit, "ctr.bump" + BrainGraph.GateSuffix)));

        Assert.Equal(("ctr.bump", null), FakeReads.Asked);
        var table = Assert.Single(Nodes(creator.App.Render()).OfType<DataTable>());
        Assert.Equal(["7", "9"], table.Rows.Select(r => r[0]));
        Assert.Contains(Nodes(creator.App.Render()).OfType<Button>(), b => b.Command is { Signal: "trace" });
    }

    [Fact]
    public async Task ASecondScenarioRunWhileOneIsInFlightIsRefused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = FakeOs();
        var creator = await os.Install<CreatorApp>();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        creator.App.RunScenarios = (_, _, _, _) => { runs++; return release.Task; };

        var first = creator.App.Scenario(new CreatorScenario("ctr"));
        await creator.App.Scenario(new CreatorScenario("ctr"));

        Assert.Equal("The scenarios of ctr are already running.", creator.App.State.Status);
        release.SetResult("✓ counts");
        await first;
        Assert.Equal(1, runs);
        Assert.Equal("✓ counts", creator.App.State.Transcript);
        Assert.Equal("scenarios", creator.App.State.InspectorTab);
    }

    [Fact]
    public async Task AScenarioRunHasATimeBudget()
    {
        await using var os = FakeOs();
        var creator = await os.Install<CreatorApp>();
        creator.App.ScenarioBudget = TimeSpan.FromMilliseconds(50);
        creator.App.RunScenarios = async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return "never"; };

        await creator.Request("scenario", new CreatorScenario("ctr"));

        Assert.StartsWith("✗ budget: the scenarios of ctr did not finish within", creator.App.State.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownGraphKeyReadsNothing()
    {
        await using var os = FakeOs();
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(Value: BrainGraph.EdgeKey("*", Unit, "other.signal")));

        Assert.Null(FakeReads.Asked);
        Assert.Contains("Not in the brain graph.", Strings(creator.App.Render()));
    }

    [Fact]
    public async Task ReplayAdvancesTheCursorAndHighlightsTheStep()
    {
        await using var os = FakeOs();
        FakeTraces.Entries =
        [
            Entry("local/ino/ino", 1, null),
            Entry("local/ctr/default", 2, "local/ino/ino:1", ReplayScript.StateChangedAlias, """{"bumps":1,"label":"a"}"""),
            Entry("local/agent/agent", 3, "local/ctr/default:2"),
            Entry("local/ctr/default", 4, "local/agent/agent:3", ReplayScript.StateChangedAlias, """{"bumps":2,"label":"a"}"""),
        ];
        var creator = await os.Install<CreatorApp>();
        await creator.Request("select", new CreatorSelect(Value: BrainGraph.PulseKey("t1")));
        Assert.Null(creator.App.State.ReplayCursor);

        await creator.Request("replay", new CreatorReplay("next"));
        Assert.Equal(0, creator.App.State.ReplayCursor);
        await creator.Request("replay", new CreatorReplay("next"));
        await creator.Request("replay", new CreatorReplay("next"));
        Assert.Equal(2, creator.App.State.ReplayCursor);
        Assert.Equal(["3"], Marked(creator));
        Assert.DoesNotContain(Nodes(creator.App.Render()).OfType<DataTable>(), t => t.Title.StartsWith("State change", StringComparison.Ordinal));

        await creator.Request("replay", new CreatorReplay("last"));
        Assert.Equal(3, creator.App.State.ReplayCursor);
        Assert.Equal(["4"], Marked(creator));
        var diff = Assert.Single(Nodes(creator.App.Render()).OfType<DataTable>(), t => t.Title.StartsWith("State change", StringComparison.Ordinal));
        var row = Assert.Single(diff.Rows);
        Assert.Equal(["bumps", "1", "2"], row);

        await creator.Request("replay", new CreatorReplay("prev"));
        Assert.Equal(2, creator.App.State.ReplayCursor);
        await creator.Request("replay", new CreatorReplay("1"));
        var first = Assert.Single(Nodes(creator.App.Render()).OfType<DataTable>(), t => t.Title.StartsWith("State change", StringComparison.Ordinal));
        Assert.Equal([["bumps", "", "1"], ["label", "", "a"]], first.Rows.Select(r => r.ToArray()));
        await creator.Request("replay", new CreatorReplay("first"));
        Assert.Equal(0, creator.App.State.ReplayCursor);

        var steps = Assert.Single(Nodes(creator.App.Render()).OfType<BrainSceneNode>()).Synapses.Where(s => s.SignalLabel!.StartsWith(ReplayScript.Prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(["replay:0:1:test.hop:t1", "replay:1:2:synapse.state-changed:t1", "replay:2:3:test.hop:t1", "replay:3:4:synapse.state-changed:t1"], steps.Select(s => s.SignalLabel));
        Assert.Equal([true, false, false, false], steps.Select(s => s.Active));
        var next = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "replay-next");
        Assert.Equal("replay", next.Command!.Signal);

        await creator.Request("replay", new CreatorReplay("stop"));
        Assert.Null(creator.App.State.ReplayCursor);
    }

    [Fact]
    public async Task SharePublishesInspectedUnitAsAppPostToThePostStore()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = FakeOs();
        os.Register<ProfileNeuron>();
        os.Register<IntoChat.OS.Modules.Apps.Social.PostsNeuron>();
        FakeSynapses.Files = new() { ["ctr.cs"] = "// the counter source" };
        FakeSynapses.SidecarFiles = new() { ["spec.md"] = "Counts bumps." };
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        var shareBtn = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "share-unit");
        Assert.Equal("share", shareBtn.Command!.Signal);

        await creator.Request("share", new CreatorShare("ctr"));

        var post = Assert.Single((await os.Get<IntoChat.OS.Modules.Apps.Social.IPosts>(NeuronId.Parse("local/social.posts/default")).Since(0, TestContext.Current.CancellationToken)).Posts);
        Assert.Equal("post-ctr", post.Id);
        Assert.Equal("ctr", post.SharedApp!.AppId);
        Assert.Equal("Counts bumps.", post.Text);
        Assert.Equal("// the counter source", post.SharedApp.Files.Single(static file => file.Path == "ctr.cs").Content);
        // For its owner only, as when the profile kept it; the profile's own list is not written.
        Assert.Empty(Assert.IsType<Audience.Only>(post.Audience).Accounts);
        Assert.Empty((await os.Get<IProfile>(NeuronId.Parse("local/os.profile/profile")).Read()).Posts);
    }

    [Fact]
    public void TraceReaderAndTraceFoldCorrelateMultiHopSpansAndRedactSecrets()
    {
        var publisher1 = NeuronId.Parse("local/ino/ino");
        var publisher2 = NeuronId.Parse("local/flows/flows/index");
        var publisher3 = NeuronId.Parse("local/agent/agent");

        var rootSignal = new JournaledSignal(
            publisher1,
            10,
            new IntoChat.OS.Apps.Ino.InoSend("Deploy with Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"),
            T0,
            TraceId: "trace-correlate-1",
            CausationId: null);

        var childSignal = new JournaledSignal(
            publisher2,
            11,
            new CreatorOpen("Use sk-proj-1234567890abcdef and vault://ANTHROPIC_API_KEY"),
            T0.AddMilliseconds(15),
            TraceId: "trace-correlate-1",
            CausationId: $"{publisher1}:10");

        var grandChildSignal = new JournaledSignal(
            publisher3,
            12,
            new CreatorSelect("watch.web"),
            T0.AddMilliseconds(30),
            TraceId: "trace-correlate-1",
            CausationId: $"{publisher2}:11");

        TraceEntry[] entries =
        [
            TraceReaderNeuron.Entry(grandChildSignal),
            TraceReaderNeuron.Entry(rootSignal),
            TraceReaderNeuron.Entry(childSignal),
        ];

        foreach (var entry in entries)
        {
            Assert.Equal("trace-correlate-1", entry.TraceId);
            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-proj-1234567890abcdef", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("vault://ANTHROPIC_API_KEY", entry.Payload, StringComparison.Ordinal);
        }

        Assert.Contains("[REDACTED]", entries[1].Payload, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entries[2].Payload, StringComparison.Ordinal);

        var hops = TraceFold.Fold(entries);
        Assert.Equal(3, hops.Length);
        Assert.Equal([10L, 11L, 12L], hops.Select(h => h.Sequence));
        Assert.Equal([0, 1, 2], hops.Select(h => h.Depth));
        Assert.Null(hops[0].Cause);
        Assert.Equal(hops[0].Address, hops[1].Cause);
        Assert.Equal(hops[1].Address, hops[2].Cause);
    }

    private static string[] Marked(InstalledApp<CreatorApp> creator)
        => [.. Nodes(creator.App.Render()).OfType<DataTable>().Single(t => t.Title.StartsWith("Trace ", StringComparison.Ordinal)).Rows
            .Select(r => r[0]).Where(c => c.StartsWith("▶ ", StringComparison.Ordinal)).Select(c => c[2..])];

    private static TraceEntry Entry(string publisher, long sequence, string? cause, string signal = "test.hop", string payload = "{}")
        => new(publisher, sequence, signal, T0.AddMilliseconds(sequence * 10), "t1", cause, payload);

    private static FlowView Build(string alias, string[] done)
        => new($"local/flow.build/{alias}", BuildFlow.Alias, $"Build {alias}", "owner", $"trace-{alias}", FlowStatus.Running,
            ["author", "draft", "verify", "deploy", "install"], done, "draft", null, null, null, 0.2, [], [], null, T0, null, null, Subject: done.Length > 0 ? alias : null);

    private static void Register(TestOS os)
    {
        os.Register<FakeFlows>();
        os.Register<FakeApprovals>();
        os.Register<FakeSynapses>();
        os.Register<FakeReads>();
        os.Register<FakeTraces>();
        os.Register<IntoChat.OS.Modules.UI.Controls.TabStripNeuron>();
    }

    private static TestOS FakeOs()
    {
        FakeFlows.Started.Clear();
        FakeFlows.Views = [];
        FakeSynapses.Files = [];
        FakeSynapses.SidecarFiles = [];
        FakeSynapses.Deployed.Clear();
        FakeReads.States.Clear();
        FakeReads.Views.Clear();
        FakeReads.Recent = [];
        FakeReads.Asked = null;
        FakeTraces.Entries = [];
        var os = TestOS.Local();
        Register(os);
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
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Strings(p.GetValue(value))),
        _ => [],
    };

    [Neuron("flows")]
    private sealed class FakeFlows : Neuron, IFlows
    {
        public static List<(string Type, string Title, string InnerJson)> Started { get; } = [];
        public static FlowView[] Views { get; set; } = [];

        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult(Views);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult(Views.FirstOrDefault(v => v.FlowId == flowId));
        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
        {
            Started.Add((type, title, innerJson));
            return Task.FromResult($"local/{type}/{Started.Count}");
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

    [Neuron("creator.reads")]
    private sealed class FakeReads : Neuron, IDebuggerReads
    {
        public static Dictionary<string, string> States { get; } = [];
        public static Dictionary<string, string> Views { get; } = [];
        public static TraceEntry[] Recent { get; set; } = [];
        public static (string Alias, string? Publisher)? Asked { get; set; }

        public Task<string?> State(string alias) => Task.FromResult(States.GetValueOrDefault(alias));
        public Task<string?> View(string alias) => Task.FromResult(Views.GetValueOrDefault(alias));
        public Task<TracePage> Signals(string alias, string? publisher, int limit) { Asked = (alias, publisher); return Task.FromResult(new TracePage(Recent)); }
        public Task<TracePage> Failures(string alias, int limit) => Task.FromResult(new TracePage([]));
        public Task<TraceEntry?> Failing(string alias) => Task.FromResult<TraceEntry?>(null);
        public Task<BrainTopology> Topology() => Task.FromResult(new BrainTopology([new(Unit, "ctr", BrainGraph.Unit)], [new("*", Unit, "ctr.bump", "reaction"), new("*", Unit, "ctr.bump", "gate")]));
        public Task<bool> Loaded(string alias) => Task.FromResult(alias == "ctr");
        public Task<long> Head() => Task.FromResult(9L);
    }

    [Neuron("trace.reader")]
    private sealed class FakeTraces : Neuron, ITraceReader
    {
        public static TraceEntry[] Entries { get; set; } = [];

        public Task<TracePage> ReadTrace(string traceId, long since, int limit) => Task.FromResult(new TracePage([.. Entries.Where(e => e.TraceId == traceId && e.Sequence > since)]));
    }
}

[Neuron("ino.synapses")]
internal sealed class FakeSynapses : Neuron, ISynapseStudio
{
    public static Dictionary<string, string> SidecarFiles { get; set; } = [];
    public static Dictionary<string, string> Files { get; set; } = [];
    public static List<(string Alias, IReadOnlyDictionary<string, string> Files)> Deployed { get; } = [];

    public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));
    public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult(""));
    public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
    {
        Deployed.Add((alias, files));
        return Task.FromResult(new UnitDeployResult(true, "", DeployStatus.Deployed, null, new Dictionary<string, InstallOutcome>()));
    }
    public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(SidecarFiles);
    public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(Files);
    public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult($"{alias} has no recorded failures.", Ok: true));
    public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
    public Task<DeployedSource[]> List() => Task.FromResult<DeployedSource[]>([]);
}
