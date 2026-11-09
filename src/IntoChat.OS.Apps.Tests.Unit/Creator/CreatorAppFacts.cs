using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Fields;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

[Collection(nameof(CreatorAppFacts))]
public sealed class CreatorAppFacts
{
    private const string Unit = "local/ctr/default";
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    public static IEnumerable<TheoryDataRow<AppOperationCase>> CreatorOperations => AppBaseline.Cases(typeof(CreatorApp), Register);

    [Theory, MemberData(nameof(CreatorOperations))]
    public Task CreatorBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task SelectingAUnitShowsItsStateViewAndSource()
    {
        await using var os = FakeOs();
        FakeReads.States["ctr"] = """{"bumps":3}""";
        FakeReads.Views["ctr"] = """{"alias":"ctr","generation":2,"view":{"title":"Counter"}}""";
        FakeSynapses.Files = new() { ["ctr.cs"] = "// the counter" };
        FakeSynapses.SidecarFiles = new() { ["spec.md"] = "Counts bumps.", ["scenarios.json"] = "[]" };
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));

        var strings = Strings(creator.App.Render()).ToArray();
        Assert.Contains("""{"bumps":3}""", strings);
        Assert.Contains(strings, s => s.Contains("Counter", StringComparison.Ordinal));
        Assert.Contains("// the counter", strings);
        Assert.Contains("Counts bumps.", strings);
        Assert.Equal(new Selection(Selection.Node, Unit), creator.App.State.Selected);
    }

    [Fact]
    public async Task SelectingAPulseShowsTheTraceTree()
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

        // The renderer selects a pulse through the select key, with the pulse key as the value.
        await creator.Request("select", new CreatorSelect(Value: BrainGraph.PulseKey("t1")));

        var table = Assert.Single(Nodes(creator.App.Render()).OfType<DataTable>());
        Assert.Equal(["1", "2", "3", "4"], table.Rows.Select(r => r[0]));
        Assert.Equal([0, 2, 4, 2], table.Rows.Select(r => r[2].Length - r[2].TrimStart().Length));
        Assert.Single(Nodes(creator.App.Render()).OfType<Progress>(), p => p.Label == "Replay");
        Assert.Equal("t1", creator.App.State.TraceId);
        var select = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == BrainGraph.SelectKey);
        Assert.Equal(new Emit("creator", "select"), select.Emit);
    }

    [Fact]
    public async Task ChangeItStartsABuildFlowWithExisting()
    {
        await using var os = FakeOs();
        FakeSynapses.Files = new() { ["ctr.cs"] = "// the counter" };
        FakeSynapses.SidecarFiles = new() { ["spec.md"] = "Counts bumps." };
        var creator = await os.Install<CreatorApp>();
        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        await Field(creator, CreatorView.ChangeField, "count by two");

        var change = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "change-it").Emit!;
        var args = EmitArgs.Of(change);
        await creator.Request(change.Signal, new CreatorChange(args[0], args[1]));

        var started = Assert.Single(FakeFlows.Started);
        Assert.Equal(BuildFlow.Alias, started.Type);
        var inner = JsonSerializer.Deserialize<BuildInner>(started.InnerJson, JsonSerializerOptions.Web)!;
        Assert.Equal("change ctr: count by two", inner.Goal);
        Assert.Equal("// the counter", inner.Existing!["ctr.cs"]);
        Assert.Equal("Counts bumps.", inner.Existing["spec.md"]);
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
        Assert.Equal(new Emit("creator", "select", JsonSerializer.SerializeToElement(BrainGraph.NodeKey(Unit))).Signal, scene.Nodes.Single(n => n.Id == Unit).Select!.Emit!.Signal);
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
        Assert.Contains(Nodes(creator.App.Render()).OfType<Button>(), b => b.Emit is { Signal: "trace" });
    }

    [Fact]
    public async Task MakeEmitsToTheStudio()
    {
        await using var os = FakeOs();
        var creator = await os.Install<CreatorApp>();
        await Field(creator, CreatorView.DescribeField, "watch elonmusk on X");

        var make = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "make").Emit!;
        await creator.Request(make.Signal, new CreatorMake(EmitArgs.Of(make)[0]));

        Assert.Equal(["watch elonmusk on X"], FakeStudio.Goals);
        Assert.Contains(FakeCompute.Records, r => r.Key == "creator" && r.Micros >= 2_500L);
        Assert.Equal("", creator.App.State.Draft);
    }

    [Fact]
    public async Task ASecondScenarioRunWhileOneIsInFlightIsRefused()
    {
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

        // The scene gets the steps in order, the current one active; the buttons are keyed and emit replay.
        var steps = Assert.Single(Nodes(creator.App.Render()).OfType<BrainSceneNode>()).Synapses.Where(s => s.SignalLabel!.StartsWith(ReplayScript.Prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(["replay:0:1:test.hop:t1", "replay:1:2:synapse.state-changed:t1", "replay:2:3:test.hop:t1", "replay:3:4:synapse.state-changed:t1"], steps.Select(s => s.SignalLabel));
        Assert.Equal([true, false, false, false], steps.Select(s => s.Active));
        var next = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "replay-next");
        Assert.Equal(new Emit("creator", "replay", JsonSerializer.SerializeToElement("next")), next.Emit);

        await creator.Request("replay", new CreatorReplay("stop"));
        Assert.Null(creator.App.State.ReplayCursor);
    }

    [Fact]
    public async Task SharePublishesInspectedUnitAsAppPostToProfile()
    {
        await using var os = FakeOs();
        os.Register<ProfileNeuron>();
        FakeSynapses.Files = new() { ["ctr.cs"] = "// the counter source" };
        FakeSynapses.SidecarFiles = new() { ["spec.md"] = "Counts bumps." };
        var creator = await os.Install<CreatorApp>();

        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey(Unit)));
        var shareBtn = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "share-unit");
        Assert.Equal("share", shareBtn.Emit!.Signal);

        await creator.Request("share", new CreatorShare("ctr"));

        var profile = await os.Get<IProfile>(NeuronId.Parse("local/os.profile/profile")).Read();
        var post = Assert.Single(profile.Posts);
        Assert.Equal("post-ctr", post.Id);
        Assert.Equal("ctr", post.TemplateAppId);
        Assert.Equal("// the counter source", post.TemplateSource);
        Assert.Equal("Counts bumps.", post.Content);
        Assert.NotNull(post.UnitFiles);
        Assert.Equal("// the counter source", post.UnitFiles!["ctr.cs"]);
    }

    [Fact]
    public async Task SeedDeploysShippedUnitTemplateThroughSynapseStudio()
    {
        await using var os = FakeOs();
        var creator = await os.Install<CreatorApp>();

        var seedBtn = Assert.Single(Nodes(creator.App.Render()).OfType<Button>(), b => b.Key == "seed:watch.web");
        Assert.Equal("seed", seedBtn.Emit!.Signal);

        await creator.Request("seed", new CreatorSeed("watch.web"));

        var deployed = Assert.Single(FakeSynapses.Deployed);
        Assert.Equal("watch.web", deployed.Alias);
        Assert.Contains("WebWatch.cs", deployed.Files.Keys);
        Assert.Contains("Deployed shipped unit watch.web (Web watch)", creator.App.State.Status, StringComparison.Ordinal);
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
            new CreatorMake("Use sk-proj-1234567890abcdef and vault://ANTHROPIC_API_KEY"),
            T0.AddMilliseconds(15),
            TraceId: "trace-correlate-1",
            CausationId: $"{publisher1}:10");

        var grandChildSignal = new JournaledSignal(
            publisher3,
            12,
            new CreatorSeed("watch.web"),
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

    private static Task Field(InstalledApp<CreatorApp> creator, string field, string value)
    {
        FakeField.Values[$"apps/creator/{field}"] = value;
        return Assert.Single(creator.App.DeclaredWatches(), w => w.SignalType == typeof(FieldChanged) && w.Key(new AppAddress("local", "creator")) == $"apps/creator/{field}")
            .Handle(new FieldChanged("local/apps/creator", field, 1));
    }

    private static void Register(TestOS os)
    {
        os.Register<FakeFlows>();
        os.Register<FakeApprovals>();
        os.Register<FakeSynapses>();
        os.Register<FakeField>();
        os.Register<FakeStudio>();
        os.Register<FakeReads>();
        os.Register<FakeTraces>();
        os.Register<FakeCompute>();
    }

    private static TestOS FakeOs()
    {
        FakeFlows.Started.Clear();
        FakeFlows.Views = [];
        FakeSynapses.Files = [];
        FakeSynapses.SidecarFiles = [];
        FakeSynapses.Deployed.Clear();
        FakeField.Values.Clear();
        FakeStudio.Goals.Clear();
        FakeReads.States.Clear();
        FakeReads.Views.Clear();
        FakeReads.Recent = [];
        FakeReads.Asked = null;
        FakeTraces.Entries = [];
        FakeCompute.Records.Clear();
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

    [Neuron("ino.studio")]
    private sealed class FakeStudio : Neuron, IInoStudio
    {
        public static List<string> Goals { get; } = [];

        public Task<InoToolResult> Make(string goal) { Goals.Add(goal); return Task.FromResult(new InoToolResult($"Started building {goal}")); }
        public Task<InoToolResult> Launch(string appId) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> List() => Task.FromResult(new InoToolResult(""));
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

    [Neuron("ui.compute")]
    private sealed class FakeCompute : Neuron, IntoChat.OS.Modules.Flutter.Compute.ICompute
    {
        public static List<(string Key, long Micros)> Records { get; } = [];
        public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> Read() => Task.FromResult(new IntoChat.OS.Modules.Flutter.Compute.ComputeState(0, DateTimeOffset.UtcNow, []));
        public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> SetBudget(long monthlyBudgetMicros) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Compute.ComputeState(0, DateTimeOffset.UtcNow, [], monthlyBudgetMicros));
        public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> Record(string key, long micros)
        {
            Records.Add((key, micros));
            return Read();
        }
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
            return Task.FromResult(new UnitDeployResult(true, "", DeployStatus.Deployed, null, new Dictionary<string, DigitalBrain.InstallOutcome>()));
        }
    public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(SidecarFiles);
    public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(Files);
    public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
    public Task<DeployedSource[]> List() => Task.FromResult<DeployedSource[]>([]);
}
