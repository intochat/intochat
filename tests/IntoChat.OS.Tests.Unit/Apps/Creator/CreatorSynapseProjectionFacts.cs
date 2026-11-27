using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Synapses;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

[Collection(nameof(CreatorSynapseProjectionFacts))]
public sealed class CreatorSynapseProjectionFacts
{
    private static readonly BrainId LocalBrain = new("local");
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private const string IssueTriageSynapseSource = """
        using DigitalBrain;
        using DigitalBrain.Neurons;
        using IntoChat.OS.Modules.Microsoft.GitHub;
        using IntoChat.OS.Modules.Microsoft.GitHub.Signals;

        [State("triage.github.issues")]
        public sealed record IssueTriageState(int TriagedCount = 0, string LastIssue = "");

        [Signal("github.issue.triaged")]
        public sealed record GithubIssueTriaged(int IssueNumber, string Mode) : Signal;

        public sealed class GithubIssueTriageSynapse : Neuron<IssueTriageState>
        {
            protected override async Task OnActivate(CancellationToken ct)
            {
                await Follow<IRepository, IssueChanged>("connection/github/main");
            }

            [On]
            public async Task OnIssueChanged(IssueChanged changed)
            {
                await Policies.Guard("github.issue.comment", 1m, async ct =>
                {
                    var repo = Get<IRepository>("connection/github/main");
                    var issue = await repo.ReadIssue(new ReadIssue(changed.Number), ct);
                    await repo.CommentOnIssue(new CommentOnIssueRequest(changed.Number, Body: "Triaged: " + issue.Snapshot.Title, Labels: ["triage"]), ct);
                });
            }
        }
        """;

    [Fact]
    public async Task SelectingIssueChangedSynapse_ProjectsLive3DGraphWithTriggerGuardEdgesPassRateRingsAndSourceTab()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = CreateOs();
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen());
        await os.Idle();

        // Left sidebar surfaces Synapses (Reaction + Workflow HStack preserved) and Units
        var initialShell = Assert.IsType<AppShellNode>(creator.App.Render().Root);
        Assert.Equal("creator-shell", initialShell.Key);
        var leftCol = Assert.IsType<Column>(initialShell.Sidebar);
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Synapses");
        var workflowBtn = leftCol.Children.OfType<Row>().SelectMany(r => r.Children).OfType<Button>()
            .Single(b => b.Key == "synapse:workflow:approval-demo");
        Assert.Equal("creator.start-workflow", workflowBtn.Command!.Signal);

        var reactionNav = leftCol.Children.OfType<NavItemNode>()
            .Single(n => n.Key == "synapse:reaction:triage.github.issues");
        Assert.NotNull(reactionNav.Command);

        // Select the deployed IssueChanged GitHub triage Synapse reaction unit
        await creator.Request("select", new CreatorSelect("synapse:reaction:triage.github.issues"));
        await os.Idle();

        var selectedShell = Assert.IsType<AppShellNode>(creator.App.Render().Root);
        var scene = Assert.IsType<BrainSceneNode>(selectedShell.Body);
        Assert.True(scene.PauseRotation);

        // 1. Trigger node (IssueChanged) -> State hub (IssueTriageState · 100%) -> Accepted & Candidate operations
        var triggerNode = Assert.Single(scene.Nodes, n => n.Id.StartsWith("trigger:", StringComparison.Ordinal) && n.Label == "IssueChanged");
        Assert.Contains("100%", triggerNode.Status, StringComparison.Ordinal);

        var hubNode = Assert.Single(scene.Nodes, n => n.Id == "state:IssueTriageState" && n.Kind == "initial");
        Assert.Contains("100%", hubNode.Status, StringComparison.Ordinal);

        Assert.Contains(scene.Nodes, n => n.Label.Contains("OnIssueChanged", StringComparison.Ordinal));

        // 2. Accordant edge metadata: accepted (Active=true) and refused ([refused: policy_guard] from real Policies.Guard call, no hardcoded IssueChanged sniffing)
        Assert.Contains(scene.Synapses, e => e.FromId == triggerNode.Id && e.ToId == hubNode.Id && e.Active);
        Assert.Contains(scene.Synapses, e => e.SignalLabel is { } lbl && lbl.Contains("[refused: policy_guard]", StringComparison.Ordinal) && !e.Active);
        Assert.DoesNotContain(scene.Synapses, e => e.SignalLabel is { } lbl && lbl.Contains("[refused: untrusted_evidence]", StringComparison.Ordinal));

        // 3. Switch the inspector's tab strip child to Source -> the live C# source from ISynapseStudio is drawn as a code block
        await creator.Screen.Dispatch(new Command(CreatorView.InspectorTabsKey, "control.select", JsonSerializer.SerializeToElement(new { id = "source" })));
        await os.Idle();

        Assert.Equal("source", creator.App.State.InspectorTab);
        var source = Assert.Single(AllNodes(creator.App.Render()).OfType<Surface>(), s => s.Kind == "code:csharp");
        Assert.Equal("IssueTriager.cs", source.Title);
        Assert.Contains("Follow<IRepository, IssueChanged>", source.Subtitle, StringComparison.Ordinal);
        Assert.Contains("CommentOnIssue", source.Subtitle, StringComparison.Ordinal);

        // 4. Run scenario with 1 pass + 1 failure -> 3D progress ring updates dynamically from 100% to 50%
        creator.App.RunScenarios = (_, _, _, _) => Task.FromResult("✓ triages opened issue\n✗ blocks untrusted injection");
        await creator.Request("scenario", new CreatorScenario("triage.github.issues"));
        await os.Idle();

        var afterScenarioScene = Assert.IsType<BrainSceneNode>(Assert.IsType<AppShellNode>(creator.App.Render().Root).Body);
        var updatedHub = Assert.Single(afterScenarioScene.Nodes, n => n.Id == "state:IssueTriageState");
        Assert.Contains("50%", updatedHub.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingIntrospectedAppAndWorkflowSynapse_Projects3DStateAndStageGraphsWithPreservedModeKeys()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = CreateOs();
        var creator = await os.Install<CreatorApp>();

        await creator.Request("open", new CreatorOpen());
        await os.Idle();

        // Creator's graph mode switcher is a control.tabStrip child offering both modes.
        var switcher = Assert.Single(AllNodes(creator.App.Render()).OfType<NeuronRef>(), r => r.NeuronUri.EndsWith("/" + CreatorView.GraphModeKey, StringComparison.Ordinal));
        var modes = switcher.Props!.Value.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(["brain", "state"], modes);

        // 1. Select compiled app node -> projects IUiIntrospector.ExploreStateGraph snapshot with 100% ring & [refused: ...] guard
        await creator.Request("select", new CreatorSelect(BrainGraph.NodeKey("local/apps/tasks")));
        await os.Idle();

        var appScene = Assert.IsType<BrainSceneNode>(Assert.IsType<AppShellNode>(creator.App.Render().Root).Body);
        Assert.Contains(appScene.Nodes, n => n.Id == "s0" && n.Status.Contains("100%", StringComparison.Ordinal));
        Assert.Contains(appScene.Nodes, n => n.Id == "s1");
        Assert.Contains(appScene.Synapses, s => s.FromId == "s0" && s.ToId == "s1" && s.Active);
        Assert.Contains(appScene.Synapses, s => s.SignalLabel is { } lbl && lbl.Contains("[refused: empty_title]", StringComparison.Ordinal) && !s.Active);

        // 2. Select Workflow synapse -> projects 3D stage pipeline with approval gate guard
        await creator.Request("select", new CreatorSelect("synapse:workflow:approval-demo"));
        await os.Idle();

        var workflowScene = Assert.IsType<BrainSceneNode>(Assert.IsType<AppShellNode>(creator.App.Render().Root).Body);
        Assert.True(workflowScene.Nodes.Count >= 3);
        Assert.Contains(workflowScene.Synapses, s => s.SignalLabel is { } lbl && lbl.Contains("[refused: person_confirmation_required]", StringComparison.Ordinal));
    }

    private static TestOS CreateOs()
    {
        var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<ProjectionFlows>();
        os.Register<ProjectionApprovals>();
        os.Register<ProjectionSynapseStudio>();
        os.Register<ProjectionSynapseCatalog>();
        os.Register<ProjectionReads>();
        os.Register<ProjectionTraces>();
        os.Register<ProjectionIntrospector>();
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
    private sealed class ProjectionFlows : Neuron, IFlows
    {
        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult<FlowView[]>([]);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult<FlowView?>(null);
        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
            => Task.FromResult("local/flow/1");
        public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
        public Task Cancel(string flowId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Neuron("approvals")]
    private sealed class ProjectionApprovals : Neuron, IApprovals
    {
        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Approval>>([]);
    }

    [Neuron("synapse.definitions")]
    private sealed class ProjectionSynapseCatalog : Neuron, ISynapseDefinitions
    {
        public Task<SynapseDefinition[]> List() => Task.FromResult<SynapseDefinition[]>(
        [
            new("reaction:triage.github.issues", SynapseForm.Reaction, "triage.github.issues", "Triages GitHub issues on IssueChanged", 3, "Succeeded"),
            new("workflow:approval-demo", SynapseForm.Workflow, "approval-demo", "Approval workflow", 2, "Succeeded"),
        ]);
    }

    [Neuron("ino.synapses")]
    private sealed class ProjectionSynapseStudio : Neuron, ISynapseStudio
    {
        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult(""));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => Task.FromResult(new UnitDeployResult(true, "", DeployStatus.Deployed, "abcdef123456", new Dictionary<string, InstallOutcome>()));
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(
            alias == "triage.github.issues"
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["spec.md"] = "# GitHub Issue Triager", ["scenarios.json"] = "[]" }
                : new Dictionary<string, string>(StringComparer.Ordinal));
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(
            alias == "triage.github.issues"
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["IssueTriager.cs"] = IssueTriageSynapseSource }
                : new Dictionary<string, string>(StringComparer.Ordinal));
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult($"{alias} has no recorded failures.", Ok: true));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
        public Task<DeployedSource[]> List() => Task.FromResult<DeployedSource[]>(
        [
            new DeployedSource("triage.github.issues", "abcdef123456", ["triage.github.issues"], DeployStatus.Deployed, null, T0),
        ]);
    }

    [Neuron("creator.reads")]
    private sealed class ProjectionReads : Neuron, IDebuggerReads
    {
        public Task<string?> State(string alias) => Task.FromResult<string?>("""{"triagedCount":3,"lastIssue":"#42"}""");
        public Task<string?> View(string alias) => Task.FromResult<string?>(null);
        public Task<TracePage> Signals(string alias, string? publisher, int limit) => Task.FromResult(new TracePage([]));
        public Task<TracePage> Failures(string alias, int limit) => Task.FromResult(new TracePage([]));
        public Task<TraceEntry?> Failing(string alias) => Task.FromResult<TraceEntry?>(null);
        public Task<BrainTopology> Topology() => Task.FromResult(new BrainTopology(
            [
                new("local/triage.github.issues/default", "triage.github.issues", BrainGraph.Unit),
                new("local/apps/tasks", "Tasks", BrainGraph.App),
            ],
            []));
        public Task<bool> Loaded(string alias) => Task.FromResult(alias == "triage.github.issues");
        public Task<long> Head() => Task.FromResult(1L);
    }

    [Neuron("trace.reader")]
    private sealed class ProjectionTraces : Neuron, ITraceReader
    {
        public Task<TracePage> ReadTrace(string traceId, long since, int limit) => Task.FromResult(new TracePage([]));
    }

    [Neuron(IUiIntrospector.NeuronType)]
    private sealed class ProjectionIntrospector : Neuron, IUiIntrospector
    {
        public Task<UiScreenSnapshot?> InspectScreen(string appId) => Task.FromResult<UiScreenSnapshot?>(null);
        public Task<UiScreenSnapshot[]> InspectOpenScreens() => Task.FromResult<UiScreenSnapshot[]>([]);

        public Task<UiStateGraphSnapshot> ExploreStateGraph(string stateOrScreenTypeName, int maxStates = 32)
        {
            if (stateOrScreenTypeName == "app.tasks")
            {
                return Task.FromResult(new UiStateGraphSnapshot
                {
                    Name = "TasksWorkspaceState",
                    TypeName = stateOrScreenTypeName,
                    IsHealthy = true,
                    States =
                    [
                        new UiStateGraphNode("s0", 0, "board"),
                        new UiStateGraphNode("s1", 1, "table"),
                    ],
                    Transitions =
                    [
                        new UiStateGraphEdge { FromId = "s0", ToId = "s1", Command = "SelectTab(table)", Accepted = true },
                        new UiStateGraphEdge { FromId = "s0", ToId = "s0", Command = "AddTask", Accepted = false, BrokenRules = ["empty_title"] },
                    ],
                });
            }

            return Task.FromResult(new UiStateGraphSnapshot());
        }

        public Task PublishStateGraph(UiStateGraphSnapshot graph) => Task.CompletedTask;
        public Task<UiStateGraphSnapshot[]> ListStateGraphs() => Task.FromResult<UiStateGraphSnapshot[]>([]);
        public Task<UiIntrospectionReport> SyncContext(string contextKey = IUiIntrospector.DefaultContextKey) => Task.FromResult(new UiIntrospectionReport());
    }
}
