using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

public sealed class Creator3PaneFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatorWorkspaceStateExecutesAggregateTransitionsAndRuleGuardsPurely()
    {
        var initial = new CreatorWorkspaceState();
        Assert.Equal("Ready", initial.Status);
        Assert.Equal("state", initial.InspectorTab);
        Assert.Equal("brain", initial.CanvasMode);
        Assert.Null(initial.Selected);
        Assert.Null(initial.ActiveRunId);
        Assert.True(initial.RequiresSelectedNode.Broken);

        var withNode = initial.SelectNode("local/myunit/default");
        Assert.NotNull(withNode.Selected);
        Assert.Equal(Selection.Node, withNode.Selected.Kind);
        Assert.Equal("local/myunit/default", withNode.Selected.Id);
        Assert.False(withNode.RequiresSelectedNode.Broken);

        var withRun = withNode.SelectRun("run-42");
        Assert.Equal("run-42", withRun.ActiveRunId);
        Assert.Contains("run-42", withRun.Status, StringComparison.Ordinal);

        var tabAccepted = withRun.SelectInspectorTab("signals");
        Assert.True(tabAccepted.IsAccepted);
        Assert.Equal("signals", tabAccepted.Next!.InspectorTab);

        var tabRefused = withRun.SelectInspectorTab("nonexistent");
        Assert.True(tabRefused.IsRefused);
        Assert.Contains("invalid_inspector_tab", tabRefused.Rejection!.Rules);

        var emptyStepRefused = withRun.StepTimeline("next", eventCount: 0);
        Assert.True(emptyStepRefused.IsRefused);
        Assert.Contains("empty_timeline", emptyStepRefused.Rejection!.Rules);

        var cleared = tabAccepted.Next.ClearSelection();
        Assert.Null(cleared.Selected);
        Assert.Null(cleared.ActiveRunId);
    }

    [Fact]
    public void CreatorViewRendersThreePanesWithTimeTravelAndInspectorNeurons()
    {
        var state = new CreatorWorkspaceState
        {
            Status = "Working",
            Selected = new(Selection.Node, "local/unit1/default"),
            ActiveRunId = "cf-run-99",
        };

        var topology = new BrainTopology(
            [new BrainTopologyNode("local/unit1/default", "Unit 1", BrainGraph.Unit)],
            []);

        FlowView[] flows =
        [
            new FlowView(
                "cf-run-99",
                FeatureToPrFlow.Alias,
                "Update Unit1",
                "admin",
                "trace-99",
                FlowStatus.Waiting,
                ["clone", "branch", "synthesize-patch", "build", "test", "diff-review", "approval-gate"],
                ["clone", "branch", "synthesize-patch", "build", "test", "diff-review"],
                "approval-gate",
                null,
                "approval-gate",
                "plan-cf-99",
                6.0 / 7.0,
                [],
                [],
                null,
                T0,
                null,
                "Waiting for approval",
                Subject: "unit1")
        ];

        Approval[] approvals =
        [
            new Approval("plan-cf-99", ApprovalKind.FlowStep, "creator", "Approve Change Run", "Change run for creator: Update Unit1", T0, T0.AddDays(2), "trace-99")
        ];

        var inspection = new Inspection(Selection.Node, "local/unit1/default")
        {
            Alias = "unit1",
            State = """{"count":5}""",
            View = """{"title":"Unit One"}""",
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["spec.md"] = "# Unit1 Specification",
                ["unit1.cs"] = "// unit1 code",
            },
        };

        var view = CreatorView.Render(state, topology, flows, approvals, inspection);
        Assert.Equal("Creator", view.Title);

        // Verify top-level structure is 3-pane SplitPane
        var rootSplit = Assert.IsType<SplitPane>(view.Root);
        var subSplit = Assert.IsType<SplitPane>(rootSplit.Trailing);

        // Left Pane assertions (clean studio workbench, no dead template seeding)
        var leftCol = Assert.IsType<Column>(rootSplit.Leading);
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Catalog Units");
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Active Change Runs");
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Studio Workbench");

        // Center Pane assertions (BrainScene + Time-Travel Transport + Active Unit + Pipeline)
        var centerCol = Assert.IsType<Column>(subSplit.Leading);
        Assert.Contains(centerCol.Children.OfType<BrainSceneNode>(), _ => true);
        Assert.Contains(centerCol.Children.OfType<Heading>(), h => h.Value == "Change Pipeline (7 Stages)");
        Assert.Contains(centerCol.Children.OfType<Heading>(), h => h.Value == "Unified Diff Review");
        Assert.Contains(centerCol.Children.OfType<Heading>(), h => h.Value == "Build & Test Summary");

        var allNodes = AllNodes(view).ToArray();
        var neuronRefs = allNodes.OfType<NeuronRef>().ToArray();
        Assert.Contains(neuronRefs, n => n.Kind == "ui.slider" && n.NeuronUri == CreatorView.ReplaySliderKey);
        Assert.Contains(neuronRefs, n => n.Kind == "ui.command-bar" && n.NeuronUri == CreatorView.ReplayCommandBarKey);
        Assert.Contains(neuronRefs, n => n.Kind == "ui.tabs" && n.NeuronUri == CreatorView.InspectorTabsKey);
        Assert.Contains(neuronRefs, n => n.Kind == "ui.property-grid" && n.NeuronUri == CreatorView.InspectorStateKey);

        // Center pane contains active unit state, view, and source
        var centerCards = centerCol.Children.OfType<Card>().ToArray();
        Assert.Contains(centerCards, c => c.Title == "State");
        Assert.Contains(centerCards, c => c.Title == "View");
        Assert.Contains(centerCards, c => c.Title == "spec.md");
        Assert.Contains(centerCards, c => c.Title == "unit1.cs");

        // Right Pane assertions (5-tab Inspector + Approvals)
        var rightButtons = allNodes.OfType<Button>().ToArray();
        Assert.Contains(rightButtons, b => b.Key == BrainGraph.SelectKey);
        Assert.Contains(rightButtons, b => b.Key == "share-unit");
        Assert.Contains(rightButtons, b => b.Key == "approve:plan-cf-99");
        Assert.Contains(rightButtons, b => b.Key == "reject:plan-cf-99");
    }

    [Fact]
    public void CreatorIdleHasZeroFakeDiffsAndLiveModeMountsStudioFrameSlot()
    {
        var idleState = new CreatorWorkspaceState();
        var topology = new BrainTopology(
            [new BrainTopologyNode("owner/apps/files", "Files", BrainGraph.App)],
            []);

        var idleView = CreatorView.Render(idleState, topology, [], [], Inspection.None);
        var idleSplit = Assert.IsType<SplitPane>(idleView.Root);
        var idleSubSplit = Assert.IsType<SplitPane>(idleSplit.Trailing);
        var idleCenter = Assert.IsType<Column>(idleSubSplit.Leading);

        Assert.DoesNotContain(idleCenter.Children.OfType<Heading>(), h => h.Value == "Unified Diff Review");
        Assert.DoesNotContain(idleCenter.Children.OfType<Heading>(), h => h.Value == "Build & Test Summary");
        Assert.DoesNotContain(idleCenter.Children.OfType<Card>(), c => c.Title == "spec.md");

        var liveState = idleState
            .SelectSelection(new Selection(Selection.Node, "owner/apps/files"))
            .SetCanvasMode("live")
            .SetPreviewViewport("720");

        var liveView = CreatorView.Render(
            liveState,
            topology,
            [],
            [],
            new Inspection(Selection.Node, "owner/apps/files") { Notice = "Compiled app." });
        var liveSplit = Assert.IsType<SplitPane>(liveView.Root);
        var liveSubSplit = Assert.IsType<SplitPane>(liveSplit.Trailing);
        var liveCenter = Assert.IsType<Column>(liveSubSplit.Leading);

        var studioColumn = liveCenter.Children.OfType<Column>().FirstOrDefault(c =>
            c.Children.OfType<Surface>().Any(s => s.Children.OfType<NeuronRef>().Any()));
        Assert.NotNull(studioColumn);

        var studioFrame = Assert.Single(studioColumn.Children.OfType<Surface>(), s => s.Children.OfType<NeuronRef>().Any());
        Assert.Equal("Live Studio · files", studioFrame.Title);
        var slot = Assert.Single(studioFrame.Children.OfType<NeuronRef>());
        Assert.Equal("owner/apps/files/view", slot.NeuronUri);
    }

    [Fact]
    public async Task ScreenDirectorySurfacesPendingApprovalsForCreator()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId("test-brain")));
        var proxies = new MockProxies();
        var services = new ServiceCollection()
            .AddSingleton<INeuronProxies>(proxies)
            .BuildServiceProvider();
        await using var os = DigitalBrain.Testing.TestOS.Local(o => o.Services = services)
            .Register(typeof(IntoChat.OS.Modules.Flutter.FlutterModule).Assembly);
        var screenDirectory = os.Get<IScreenDirectory>(new NeuronId(new BrainId("test-brain"), IScreenDirectory.TypeName, IScreenDirectory.Key("test-brain")));

        var summaries = await screenDirectory.List();
        var creatorSummary = Assert.Single(summaries, s => s.AppId == "creator");
        Assert.Contains(summaries, s => s.AppId == "uikit");
        Assert.Equal(1, creatorSummary.PendingApprovals);

        var snapshot = await screenDirectory.Read("creator");
        Assert.NotNull(snapshot);
        var pendingItem = Assert.Single(snapshot.PendingApprovals);
        Assert.Equal("plan-cf-77", pendingItem.ApprovalId);
        Assert.Equal("creator", pendingItem.AppId);

        var result = await screenDirectory.Approve("plan-cf-77", true);
        Assert.True(result.Accepted);
        Assert.Contains("plan-cf-77", proxies.Inbox.Resolved.Select(r => r.PlanId));
    }

    [Fact]
    public void CreatorViewDoesNotFabricateRoslynOrScenarioVerificationOnCompletedF2prFlow()
    {
        var state = new CreatorWorkspaceState();
        var topology = new BrainTopology([], []);
        FlowView[] flows =
        [
            new FlowView(
                "cf-run-100",
                FeatureToPrFlow.Alias,
                "Update Unit1",
                "admin",
                "trace-100",
                FlowStatus.Completed,
                FeatureToPrFlow.Names,
                FeatureToPrFlow.Names,
                null,
                null,
                null,
                "plan-cf-100",
                1.0,
                [],
                [],
                null,
                T0,
                T0,
                "Published draft PR",
                Subject: "unit1")
        ];

        var view = CreatorView.Render(state, topology, flows, [], Inspection.None);
        var viewJson = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain("Roslyn synapse compile passed", viewJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scenario verification passed", viewJson, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Node> AllNodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => AllNodes(p.GetValue(node)))],
        View view => AllNodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(AllNodes),
        _ => [],
    };

    private sealed class MockProxies : INeuronProxies
    {
        public MockInbox Inbox { get; } = new();
        public MockApprovalsNeuron Approvals { get; } = new();

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IApprovals)) { return (T)(object)Approvals; }
            if (typeof(T) == typeof(IConfirmationInbox)) { return (T)(object)Inbox; }
            if (typeof(T) == typeof(IConfirmationRequests)) { return (T)(object)Inbox; }
            if (typeof(T) == typeof(IWorkspace)) { return (T)(object)new MockWorkspaceStub(); }
            if (typeof(T) == typeof(IView)) { return (T)(object)new MockViewStub(); }
            throw new KeyNotFoundException($"Mock neuron for {id} not configured");
        }
    }

    private sealed class MockViewStub : IView
    {
        public Task Publish(long generation, View view) => Task.CompletedTask;

        public Task<PublishedView?> Read()
            => Task.FromResult<PublishedView?>(new PublishedView("creator", 1L, System.Text.Json.JsonSerializer.SerializeToElement(new View("Creator", new Text("Ready")))));

        public Task<ViewActivation> Activate(long generation, string key, string? fieldValue)
            => Task.FromResult(new ViewActivation(ViewActivationKind.Intent, Intent: new Intent(default, "noop"), Value: fieldValue));
    }

    private sealed class MockApprovalsNeuron : IApprovals
    {
        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
        {
            IReadOnlyList<Approval> list =
            [
                new Approval(
                    PlanId: "plan-cf-77",
                    Kind: ApprovalKind.FlowStep,
                    Subject: "creator",
                    Title: "Approve Change",
                    Detail: "Change run for creator: Refactor auth",
                    Requested: DateTimeOffset.UtcNow,
                    Expires: DateTimeOffset.UtcNow.AddDays(1),
                    TraceId: "trace-77")
            ];
            return Task.FromResult(list);
        }
    }

    private sealed class MockInbox : IConfirmationInbox, IConfirmationRequests
    {
        private readonly HashSet<string> _pending = new(["plan-cf-77"], StringComparer.Ordinal);
        private readonly Dictionary<string, ConfirmationResult> _outcomes = new(StringComparer.Ordinal);
        public List<(string PlanId, bool Approved)> Resolved { get; } = [];

        public Task<ConfirmationInbox> Read() => Task.FromResult(new ConfirmationInbox(
            0L,
            [.. _pending.Select(planId => new ConfirmationRequested(
                planId,
                new ConfirmationPlan(planId, ConfirmationAction.Other, false, planId, planId, "Approve", "Deny", planId),
                ConfirmationPrincipal.App("creator"),
                default,
                DateTimeOffset.UtcNow.AddHours(1)))]));

        public Task Resolve(string planId, bool approved)
        {
            Resolved.Add((planId, approved));
            _pending.Remove(planId);
            _outcomes[planId] = new ConfirmationResult(
                planId,
                approved ? ConfirmationOutcome.Approved : ConfirmationOutcome.Denied,
                approved ? ConfirmationDenyReason.None : ConfirmationDenyReason.ByPerson,
                Skipped: false);
            return Task.CompletedTask;
        }

        public Task<ConfirmationResult?> Request(ConfirmationPlan plan) => Task.FromResult<ConfirmationResult?>(null);

        public Task<ConfirmationResult?> Outcome(string planId)
            => Task.FromResult(_outcomes.TryGetValue(planId, out var outcome) ? outcome : null);

        public Task Withdraw(string planId)
        {
            _pending.Remove(planId);
            return Task.CompletedTask;
        }
    }

    private sealed class MockWorkspaceStub : IWorkspace
    {
        public Task<WorkspaceState> Apply(WorkspaceUpdate next, string operationId, long expectedRevision) => Task.FromResult(new WorkspaceState(1L, ["owner"], []));
        public Task<WorkspaceState> Show(string app, string operationId, long expectedRevision) => Task.FromResult(new WorkspaceState(1L, ["owner"], []));
        public Task<WorkspaceState> Back(string operationId, long expectedRevision) => Task.FromResult(new WorkspaceState(1L, ["owner"], []));
        public Task<WorkspaceState> Home(string operationId, long expectedRevision) => Task.FromResult(new WorkspaceState(1L, ["owner"], []));
        public Task<WorkspaceState> Close(string app, string operationId, long expectedRevision) => Task.FromResult(new WorkspaceState(1L, ["owner"], []));
        public Task<WorkspaceState> Read() => Task.FromResult(new WorkspaceState(1L, ["owner"], []));
    }
}
