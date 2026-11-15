using DigitalBrain;
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

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

public sealed class Creator3PaneFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatorDecideExecutesStateTransitionsPurely()
    {
        var initial = new CreatorState();
        Assert.Equal("Ready", initial.Status);
        Assert.Null(initial.Selected);
        Assert.Null(initial.ActiveRunId);

        var withNode = CreatorDecide.SelectNode(initial, "local/myunit/default");
        Assert.NotNull(withNode.Selected);
        Assert.Equal(Selection.Node, withNode.Selected.Kind);
        Assert.Equal("local/myunit/default", withNode.Selected.Id);

        var withRun = CreatorDecide.SelectRun(withNode, "run-42");
        Assert.Equal("run-42", withRun.ActiveRunId);
        Assert.Contains("run-42", withRun.Status);

        var withDraft = CreatorDecide.SetDraft(withRun, "build counter");
        Assert.Equal("build counter", withDraft.Draft);

        var withChangeDraft = CreatorDecide.SetChangeDraft(withDraft, "add reset button");
        Assert.Equal("add reset button", withChangeDraft.ChangeDraft);

        var cleared = CreatorDecide.ClearSelection(withChangeDraft);
        Assert.Null(cleared.Selected);
        Assert.Null(cleared.ActiveRunId);
    }

    [Fact]
    public void CreatorViewRendersThreePanesWithAllComponents()
    {
        var state = new CreatorState
        {
            Status = "Working",
            Selected = new(Selection.Node, "local/unit1/default"),
            Draft = "my unit",
            ChangeDraft = "optimize performance",
            ActiveRunId = "cf-run-99"
        };

        var topology = new BrainTopology(
            [new BrainTopologyNode("local/unit1/default", "Unit 1", BrainGraph.Unit)],
            []);

        FlowView[] flows =
        [
            new FlowView("cf-run-99", ChangeFlow.Alias, "Update Unit1", "admin", "trace-99", FlowStatus.Waiting, ["clone", "branch", "synthesize-patch", "build", "test", "diff-review", "approval-gate"], ["clone", "branch", "synthesize-patch", "build", "test", "diff-review"], "approval-gate", null, "approval-gate", "plan-cf-99", 6.0 / 7.0, [], [], null, T0, null, "Waiting for approval", Subject: "unit1")
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
            Files = new Dictionary<string, string>
            {
                ["spec.md"] = "# Unit1 Specification",
                ["unit1.cs"] = "// unit1 code"
            }
        };

        var view = CreatorView.Render(state, topology, flows, approvals, inspection);
        Assert.Equal("Creator", view.Title);

        // Verify top-level structure is 3-pane SplitPane
        var rootSplit = Assert.IsType<SplitPane>(view.Root);
        var subSplit = Assert.IsType<SplitPane>(rootSplit.Trailing);

        // Left Pane assertions
        var leftCol = Assert.IsType<Column>(rootSplit.Leading);
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Catalog Units");
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Active Change Runs");
        Assert.Contains(leftCol.Children.OfType<Heading>(), h => h.Value == "Make New Unit");

        // Center Pane assertions
        var centerCol = Assert.IsType<Column>(subSplit.Leading);
        Assert.Contains(centerCol.Children.OfType<BrainSceneNode>(), _ => true);
        Assert.Contains(centerCol.Children.OfType<Heading>(), h => h.Value == "Change Pipeline (7 Stages)");
        Assert.Contains(centerCol.Children.OfType<Heading>(), h => h.Value == "Unified Diff Review");
        Assert.Contains(centerCol.Children.OfType<Heading>(), h => h.Value == "Build & Test Summary");

        // Center pane contains active unit state, view, and source
        var centerCards = centerCol.Children.OfType<Card>().ToArray();
        Assert.Contains(centerCards, c => c.Title == "State");
        Assert.Contains(centerCards, c => c.Title == "View");
        Assert.Contains(centerCards, c => c.Title == "spec.md");
        Assert.Contains(centerCards, c => c.Title == "unit1.cs");

        // Right Pane assertions (Inspector + Change Controls + Approvals)
        var rightCol = Assert.IsType<Column>(subSplit.Trailing);
        var rightButtons = rightCol.Children
            .Concat(rightCol.Children.OfType<Row>().SelectMany(r => r.Children))
            .OfType<Button>()
            .ToArray();
        Assert.Contains(rightButtons, b => b.Key == BrainGraph.SelectKey);
        Assert.Contains(rightCol.Children.OfType<Field>(), f => f.Name == CreatorView.FieldKey(CreatorView.ChangeField));
        Assert.Contains(rightButtons, b => b.Key == "change-it");
        Assert.Contains(rightButtons, b => b.Key == "approve:plan-cf-99");
        Assert.Contains(rightButtons, b => b.Key == "reject:plan-cf-99");
    }

    [Fact]
    public void CreatorIdleHasZeroFakeDiffsAndLiveModeMountsStudioFrameSlot()
    {
        var idleState = new CreatorState();
        var topology = new BrainTopology(
            [new BrainTopologyNode("owner/apps/files", "Files", BrainGraph.App)],
            []);

        var idleView = CreatorView.Render(idleState, topology, [], [], new Inspection("", ""));
        var idleSplit = Assert.IsType<SplitPane>(idleView.Root);
        var idleSubSplit = Assert.IsType<SplitPane>(idleSplit.Trailing);
        var idleCenter = Assert.IsType<Column>(idleSubSplit.Leading);

        // When no ChangeFlow is running, Creator must NOT render fake spec.md diffs or fake 100% pass cards!
        Assert.DoesNotContain(idleCenter.Children.OfType<Heading>(), h => h.Value == "Unified Diff Review");
        Assert.DoesNotContain(idleCenter.Children.OfType<Heading>(), h => h.Value == "Build & Test Summary");
        Assert.DoesNotContain(idleCenter.Children.OfType<Card>(), c => c.Title == "spec.md");

        // Selecting an installed app and switching to Live Studio mode mounts its View via NeuronRef inside StudioFrame!
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
        using var person = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(
            DigitalBrain.Capabilities.CapabilityCallers.Of("alice", "alice", "test-brain"));
        var proxies = new MockProxies();
        var screenDirectory = new ScreenDirectoryNeuron(proxies, "test-brain");

        var summaries = await screenDirectory.List();
        var creatorSummary = Assert.Single(summaries, s => s.AppId == "creator");
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

    private sealed class MockProxies : INeuronProxies
    {
        public MockInbox Inbox { get; } = new();
        public MockApprovalsNeuron Approvals { get; } = new();

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IApprovals))
            {
                return (T)(object)Approvals;
            }
            if (typeof(T) == typeof(IConfirmationInbox))
            {
                return (T)(object)Inbox;
            }
            if (typeof(T) == typeof(IWorkspace))
            {
                return (T)(object)new MockWorkspaceStub();
            }
            if (typeof(T) == typeof(IView))
            {
                return (T)(object)new MockViewStub();
            }
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

    private sealed class MockInbox : IConfirmationInbox
    {
        public List<(string PlanId, bool Approved)> Resolved { get; } = [];

        public Task<ConfirmationInbox> Read()
            => Task.FromResult(new ConfirmationInbox(0L, []));

        public Task Resolve(string planId, bool approved)
        {
            Resolved.Add((planId, approved));
            return Task.CompletedTask;
        }
    }

    private sealed class MockWorkspaceStub : IntoChat.OS.Modules.Flutter.Workspace.IWorkspace
    {
        public Task<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState> Apply(IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate next, string operationId, long expectedRevision) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1L, ["owner"], []));
        public Task<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState> Show(string app, string operationId, long expectedRevision) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1L, ["owner"], []));
        public Task<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState> Back(string operationId, long expectedRevision) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1L, ["owner"], []));
        public Task<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState> Home(string operationId, long expectedRevision) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1L, ["owner"], []));
        public Task<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState> Close(string app, string operationId, long expectedRevision) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1L, ["owner"], []));
        public Task<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState> Read() => Task.FromResult(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(1L, ["owner"], []));
    }

    [Fact]
    public void CreatorViewDoesNotFabricateRoslynOrScenarioVerificationOnCompletedF2prFlow()
    {
        var state = new CreatorState();
        var topology = new BrainTopology([], []);
        FlowView[] flows =
        [
            new FlowView(
                "cf-run-100",
                ChangeFlow.Alias,
                "Update Unit1",
                "admin",
                "trace-100",
                FlowStatus.Completed,
                ["prepare-worktree", "edit", "verify-and-commit", "approval-gate", "publish"],
                ["prepare-worktree", "edit", "verify-and-commit", "approval-gate", "publish"],
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

        var view = CreatorView.Render(state, topology, flows, [], new Inspection("", ""));
        var viewJson = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain("Roslyn synapse compile passed", viewJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scenario verification passed", viewJson, StringComparison.OrdinalIgnoreCase);
    }
}
