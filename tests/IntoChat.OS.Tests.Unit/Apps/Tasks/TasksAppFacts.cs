using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Tasks;

[Collection(nameof(TasksAppFacts))]
public sealed class TasksAppFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly BrainId LocalBrain = new("local");

    private static FlowView Flow(
        string key,
        FlowStatus status,
        string type = "flow.note",
        FlowRelation[]? relations = null,
        DateTimeOffset? started = null,
        DateTimeOffset? ended = null,
        double fraction = 0.5,
        WaitReason? waiting = null)
        => new(
            $"local/{type}/{key}",
            type,
            $"Task {key}",
            "owner",
            $"trace-{key}",
            status,
            ["draft", "approve", "start"],
            ["draft"],
            "approve",
            waiting,
            waiting is null ? null : "approve",
            null,
            fraction,
            relations ?? [],
            [],
            null,
            started ?? T0,
            ended,
            null,
            "ino");

    // ── AggregateExplorer & Domain Rules ─────────────────────────────────────

    [Fact]
    public void TasksWorkspaceState_AggregateExplorer_UnfoldsHealthyGraphAndRuleGuards()
    {
        var graph = AggregateExplorer<TasksWorkspaceState>.ExploreScreen<TasksApp>(maxStates: 32).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Contains(graph.States, s => s.State.ActiveTab == "board");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "list");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "details");
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("empty_title", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_stage", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("task_not_found", StringComparer.Ordinal));
    }

    [Fact]
    public void DomainRules_RefuseEmptyTitleInvalidStageMissingTaskAndCompletedAdvance()
    {
        var initial = new TasksWorkspaceState();

        var emptyTitle = initial.AddTask("   ");
        Assert.True(emptyTitle.IsRefused);
        Assert.Contains("empty_title", emptyTitle.Rejection!.Rules);

        var invalidStage = initial.AddTask("Valid title", stage: "unknown-lane");
        Assert.True(invalidStage.IsRefused);
        Assert.Contains("invalid_stage", invalidStage.Rejection!.Rules);

        var missingTask = initial.MoveTask("non-existent", "review");
        Assert.True(missingTask.IsRefused);
        Assert.Contains("task_not_found", missingTask.Rejection!.Rules);

        var completedSelected = initial.SelectTaskOrFlow("task-1");
        var advanceDone = completedSelected.AdvanceSelectedStage();
        Assert.True(advanceDone.IsRefused);
        Assert.Contains("cannot_advance_task", advanceDone.Rejection!.Rules);

        var afterClear = initial.ClearDoneTasks();
        Assert.True(afterClear.IsAccepted);
        var clearAgain = afterClear.Next!.ClearDoneTasks();
        Assert.True(clearAgain.IsRefused);
        Assert.Contains("no_completed_tasks", clearAgain.Rejection!.Rules);
    }

    // ── Live Modules.UI Composition (Kanban, Stepper, Tabs, Table, CommandBar) ──

    [Fact]
    public async Task KanbanBoard_MountsFourCanonicalColumnsAndPipelinedMovesSyncWithoutClobbering()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        var tasks = await os.Install<TasksApp>();

        var kanban = os.Get<IKanbanBoard>(new NeuronId(LocalBrain, "ui.kanban", TasksApp.KanbanKey));
        var initialBoard = await kanban.Read();
        Assert.Equal(["backlog", "in-progress", "review", "done"], initialBoard.Columns.Select(c => c.Id));
        Assert.Equal(["Backlog", "In Progress", "Review", "Done"], initialBoard.Columns.Select(c => c.Title));
        Assert.Contains(initialBoard.Cards, c => c.Id == "task-3" && c.ColumnId == "backlog");

        // Fire pipelined back-to-back sequential moves on IKanbanBoard and verify anti-clobber sync.
        var move1 = kanban.MoveCard("task-3", "in-progress");
        var move2 = kanban.MoveCard("task-3", "review");
        var move3 = kanban.MoveCard("task-3", "done");
        await Task.WhenAll(move1, move2, move3);
        await os.Idle();

        Assert.Equal("task-3", tasks.App.State.SelectedTaskId);
        Assert.Equal("done", tasks.App.State.SelectedTask?.Stage);

        var finalBoard = await kanban.Read();
        Assert.Equal("done", finalBoard.Cards.Single(c => c.Id == "task-3").ColumnId);

        var stepper = os.Get<IStepper>(new NeuronId(LocalBrain, "ui.stepper", TasksApp.StepperKey));
        var stepperState = await stepper.Read();
        Assert.Equal("done", stepperState.ActiveStepId);
    }

    [Fact]
    public async Task Stepper_SelectAndAdvanceTransitionSelectedTaskAndSyncKanban()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        var tasks = await os.Install<TasksApp>();

        await tasks.Request("select", new TasksSelect("task-3"));
        Assert.Equal("backlog", tasks.App.State.SelectedTask?.Stage);

        var stepper = os.Get<IStepper>(new NeuronId(LocalBrain, "ui.stepper", TasksApp.StepperKey));
        var kanban = os.Get<IKanbanBoard>(new NeuronId(LocalBrain, "ui.kanban", TasksApp.KanbanKey));

        // Advance backlog -> in-progress -> review back-to-back
        await stepper.Advance();
        await stepper.Advance();
        await os.Idle();

        Assert.Equal("review", tasks.App.State.SelectedTask?.Stage);
        Assert.Equal("review", (await kanban.Read()).Cards.Single(c => c.Id == "task-3").ColumnId);

        // Select "done" directly on IStepper
        await stepper.Select("done");
        await os.Idle();

        Assert.Equal("done", tasks.App.State.SelectedTask?.Stage);
        Assert.Equal("done", (await kanban.Read()).Cards.Single(c => c.Id == "task-3").ColumnId);
    }

    [Fact]
    public async Task Tabs_CommandBar_And_Table_DriveWorkspaceStateCleanly()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        var tasks = await os.Install<TasksApp>();

        var tabs = os.Get<ITabs>(new NeuronId(LocalBrain, "ui.tabs", TasksApp.TabsKey));
        var cmdBar = os.Get<ICommandBar>(new NeuronId(LocalBrain, "ui.command-bar", TasksApp.CommandBarKey));
        var table = os.Get<ITable>(new NeuronId(LocalBrain, "ui.table", TasksApp.TableKey));
        var kanban = os.Get<IKanbanBoard>(new NeuronId(LocalBrain, "ui.kanban", TasksApp.KanbanKey));

        // Pipelined tab switches: board -> table -> approvals -> legacy list/details
        await tabs.Select("table");
        await tabs.Select("approvals");
        await os.Idle();
        Assert.Equal("approvals", tasks.App.State.ActiveTab);
        var detailsWire = (await tasks.Screen.Read())!.View.GetRawText();
        Assert.Contains("Task Details", detailsWire, StringComparison.Ordinal);

        await tabs.Select("table");
        await os.Idle();
        Assert.Equal("table", tasks.App.State.ActiveTab);
        var listWire = (await tasks.Screen.Read())!.View.GetRawText();
        Assert.Contains(TasksApp.TableKey, listWire, StringComparison.Ordinal);

        await tasks.Request("view", new TasksView("list"));
        Assert.Equal("list", tasks.App.State.ActiveTab);
        await tasks.Request("view", new TasksView("board"));
        Assert.Equal("board", tasks.App.State.ActiveTab);

        // Select row 2 ("task-3") from ITable
        await table.SelectRow(2);
        await os.Idle();
        Assert.Equal("task-3", tasks.App.State.SelectedTaskId);

        // CommandBar actions: add-task, advance-stage, complete-task, clear-done
        var beforeCount = tasks.App.State.Tasks.Length;
        await cmdBar.Invoke("add-task");
        await os.Idle();
        Assert.Equal(beforeCount + 1, tasks.App.State.Tasks.Length);
        var addedId = tasks.App.State.SelectedTaskId!;
        Assert.Equal("backlog", tasks.App.State.SelectedTask?.Stage);

        await cmdBar.Invoke("advance-stage");
        await os.Idle();
        Assert.Equal("in-progress", tasks.App.State.SelectedTask?.Stage);

        await cmdBar.Invoke("complete-task");
        await os.Idle();
        Assert.Equal("done", tasks.App.State.SelectedTask?.Stage);

        await cmdBar.Invoke("clear-done");
        await os.Idle();
        Assert.DoesNotContain(tasks.App.State.Tasks, t => t.Stage == "done");
        Assert.DoesNotContain((await kanban.Read()).Cards, c => c.Id == addedId);
    }

    // ── Live Brain Flows, Timeline, Approvals & Synapse Studio Integration ───

    [Fact]
    public void BoardColumnsMapStatusesIntoFourCanonicalStages()
    {
        FlowView[] flows =
        [
            Flow("a", FlowStatus.Pending),
            Flow("b", FlowStatus.Running),
            Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval),
            Flow("d", FlowStatus.Blocked),
            Flow("e", FlowStatus.Completed),
            Flow("f", FlowStatus.Failed),
            Flow("g", FlowStatus.Cancelled),
        ];

        var state = new TasksWorkspaceState("board", new FlowQuery(), null, []).SyncLiveFlows(flows);
        var columns = state.Columns(flows);

        Assert.Equal(["backlog", "in-progress", "review", "done"], columns.Select(c => c.Id));
        Assert.Equal(["Task a", "Task d"], columns[0].Items.Select(i => i.Title));
        Assert.Equal(["Task b"], columns[1].Items.Select(i => i.Title));
        Assert.Equal(["Task c"], columns[2].Items.Select(i => i.Title));
        Assert.Equal(["Task e", "Task f", "Task g"], columns[3].Items.Select(i => i.Title));
    }

    [Fact]
    public void TimelineLaysFollowsWithLag()
    {
        var target = Flow("t", FlowStatus.Completed, started: T0, ended: T0.AddDays(2));
        var dependant = Flow("d", FlowStatus.Pending, started: T0, ended: T0.AddDays(1),
            relations: [new FlowRelation(target.FlowId, RelationKind.Follows, TimeSpan.FromDays(1))]);

        var bars = TasksWorkspaceState.Bars([target, dependant], T0.AddDays(3));

        var bar = Assert.Single(bars, b => b.FlowId == dependant.FlowId);
        Assert.Equal(T0.AddDays(3), bar.Start);
        Assert.Equal(T0.AddDays(4), bar.End);
    }

    [Fact]
    public void CardShowsStepsAsReadOnlyWithoutResolveAction()
    {
        var flow = Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval);
        var approval = new Approval("plan-1", ApprovalKind.FlowStep, flow.FlowId, "Approve plan", "Three subtasks", T0, T0.AddDays(2), flow.TraceId);

        var side = TasksApp.RenderSideCore(new TasksWorkspaceState("board", new FlowQuery(), flow.FlowId, []), [flow], [approval], null);

        var strings = Strings(side).ToArray();
        Assert.Contains("✓ draft", strings);
        Assert.Contains("● approve", strings);
        Assert.Contains("○ start", strings);
        Assert.Null(Assert.Single(Nodes(side).OfType<ApprovalCardNode>()).Resolve);
    }

    [Fact]
    public async Task NewTaskStartsPlanFlow()
    {
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();

        await tasks.Request("new", new TasksNew("Ship docs", "Write and publish the docs"));

        var started = Assert.Single(FakeFlows.Started);
        Assert.Equal(PlanFlow.Alias, started.Type);
        Assert.Equal("Ship docs", started.Title);
        Assert.Equal("Write and publish the docs", JsonDocument.Parse(started.InnerJson).RootElement.GetProperty("goal").GetString());
        Assert.Equal(started.Id, tasks.App.State.Selected);
    }

    [Fact]
    public void OpenTraceNavigatesToTheCreator()
    {
        var flow = Flow("c", FlowStatus.Running);
        var side = TasksApp.RenderSideCore(new TasksWorkspaceState("board", new FlowQuery(), flow.FlowId, []), [flow], [], null);

        var open = Assert.Single(Nodes(side).OfType<Button>(), b => b.Label == "Open trace").Intent;

        Assert.Equal(new Intent(IntentKind.OpenWindow, "os://window/open?app=creator&input=trace%2Ftrace-c"), open);
    }

    [Fact]
    public void FormFieldsAreTheAppsTextfieldNeurons()
    {
        var view = ScreenPreview.With<TasksApp, View>(app => app.Render());
        var names = Nodes(view).OfType<Field>().Select(f => f.Name).ToArray();
        Assert.Equal(["apps/tasks/title", "apps/tasks/description"], names);
    }

    [Fact]
    public async Task NewTaskFormDeliversTitleAndDescription()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOs();
        var tasks = await os.Install<TasksApp>();

        await Field(tasks, "title", "Ship docs");
        await Field(tasks, "description", "Write and publish the docs");
        var start = Assert.Single(Nodes(tasks.App.Render()).OfType<Button>(), b => b.Label == "Start").Command!;
        var startArgs = EmitArgs.Of(start);
        await tasks.Request(start.Signal, new TasksNew(startArgs[0], startArgs[1]));

        var started = Assert.Single(FakeFlows.Started);
        Assert.Equal("Ship docs", started.Title);
        Assert.Equal("Write and publish the docs", JsonDocument.Parse(started.InnerJson).RootElement.GetProperty("goal").GetString());
    }

    [Fact]
    public async Task CancelCallsFlowsAndPublishedBoardExposesNoCancelKey()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("c", FlowStatus.Running)];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        await tasks.Request("select", new TasksSelect(FakeFlows.Views[0].FlowId));

        Assert.DoesNotContain(Nodes(tasks.App.Render()).OfType<Button>(), b => b.Key == "cancel");
        Assert.Contains(Nodes(tasks.App.Render()).OfType<Button>(), b => b.Key == $"open:{FakeFlows.Views[0].FlowId}");

        var cancel = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Button>(), b => b.Label == "Cancel").Command!;
        await tasks.Request(cancel.Signal, new TasksCancel(EmitArgs.Of(cancel)[0]));

        Assert.Equal(FakeFlows.Views[0].FlowId, Assert.Single(FakeFlows.Cancels));
    }

    [Fact]
    public async Task RefusedCancelShowsWhy()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("c", FlowStatus.Waiting, waiting: WaitReason.Approval)];
        var flowId = FakeFlows.Views[0].FlowId;
        FakeFlows.CancelRefusal = $"flow {flowId} waits for a person at approve: only a person cancels it";
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request("cancel", new TasksCancel(flowId));

        Assert.Empty(FakeFlows.Cancels);
        Assert.Contains($"Could not cancel: {FakeFlows.CancelRefusal}", Strings(tasks.App.Render()));
    }

    [Fact]
    public async Task RelateAddsRelation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("a", FlowStatus.Pending), Flow("b", FlowStatus.Running)];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        await tasks.Request("select", new TasksSelect(FakeFlows.Views[0].FlowId));

        // The side view that drew the two relation selects is published by nothing; its operations are the screen's signals.
        await tasks.Request("relate-kind", new TasksRelateKind("Follows"));
        await Field(tasks, "lag", "1");
        await tasks.Request("relate", new TasksRelate(FakeFlows.Views[0].FlowId, tasks.App.State.RelateKind, FakeFlows.Views[1].FlowId, tasks.App.State.Draft.Lag));

        var (flowId, relation) = Assert.Single(FakeFlows.Relations);
        Assert.Equal(FakeFlows.Views[0].FlowId, flowId);
        Assert.Equal(new FlowRelation(FakeFlows.Views[1].FlowId, RelationKind.Follows, TimeSpan.FromDays(1)), relation);
    }

    [Fact]
    public async Task ListFiltersByQuery()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        FakeFlows.Views =
        [
            Flow("a", FlowStatus.Running) with { Owner = "ann" },
            Flow("b", FlowStatus.Running) with { Owner = "bob" },
            Flow("c", FlowStatus.Completed) with { Owner = "ann" },
        ];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        await tasks.Request("filter", new TasksFilter(Owner: "ann", Status: "Running"));

        var table = os.Get<ITable>(new NeuronId(LocalBrain, "ui.table", TasksApp.TableKey));
        var tableState = await table.Read();
        var row = Assert.Single(tableState.Rows);
        Assert.Equal("Task a", row[1]);
    }

    [Fact]
    public async Task ShowSourceRendersUnitMetadataAndOrderedFiles()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("unit1", FlowStatus.Completed, type: "flow.build")];
        FakeSynapses.Sources = [new DeployedSource("unit1", "h1", ["weather"], DeployStatus.Deployed, null, T0)];
        FakeSynapses.Files = new() { ["B.cs"] = "class B;", ["A.cs"] = "class A;" };
        FakeSynapses.SidecarFiles = new() { ["scenarios.json"] = "[]", ["spec.md"] = "# Weather" };
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request("show-source", new TasksShowSource(FakeFlows.Views[0].FlowId));

        var body = Assert.Single(Nodes(tasks.App.RenderSide()).OfType<Card>(), c => c.Title == "Source").Body!;
        Assert.Contains("unit1", body, StringComparison.Ordinal);
        Assert.Contains("h1", body, StringComparison.Ordinal);
        Assert.Contains("weather", body, StringComparison.Ordinal);
        var order = new[] { "--- spec.md ---", "--- A.cs ---", "--- B.cs ---", "--- scenarios.json ---" }
            .Select(marker => body.IndexOf(marker, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0, body));
        Assert.Equal(order.Order(), order);
    }

    private static FlowView Unit(FlowStatus status, string? current, string[] done, WaitReason? waiting = null, string? waitingStep = null, string? plan = null, string? detail = null)
        => Flow("u", status, type: "flow.build") with
        {
            Steps = ["author", "draft", "verify", "deploy", "install"],
            Done = done,
            Current = current,
            WaitingFor = waiting,
            WaitingStep = waitingStep,
            ConfirmationPlanId = plan,
            Detail = detail,
        };

    [Fact]
    public void BuildFlowSideDiagnosticsCoverInstallConsentMissingConnectionAndFailedVerify()
    {
        var installFlow = Unit(FlowStatus.Waiting, "install", ["author", "draft", "verify", "deploy"], WaitReason.External, "install", "plan-i");
        var consent = new Approval("plan-i", ApprovalKind.SynapseStaged, "xwatch", "Install xwatch?", "Reads posts. Reads api.x.com.", T0, T0.AddDays(2), installFlow.TraceId);
        var installSide = TasksApp.RenderSideCore(new TasksWorkspaceState("board", new FlowQuery(), installFlow.FlowId, []), [installFlow], [consent], null);
        Assert.Contains("Reads posts. Reads api.x.com.", Strings(installSide));
        Assert.All(Nodes(installSide).OfType<ApprovalCardNode>(), card => Assert.Null(card.Resolve));

        var deployFlow = Unit(FlowStatus.Waiting, "deploy", ["author", "draft", "verify"], WaitReason.External, "deploy", detail: "connect http@1 for api.x.com");
        var deploySide = TasksApp.RenderSideCore(new TasksWorkspaceState("board", new FlowQuery(), deployFlow.FlowId, []), [deployFlow], [], null);
        var openConn = Assert.Single(Nodes(deploySide).OfType<Button>(), b => b.Key == "open-connections").Intent;
        Assert.Equal(new Intent(IntentKind.OpenSettings, "connections"), openConn);

        var verifyFlow = Unit(FlowStatus.Failed, "verify", ["author", "draft"],
            detail: "The unit did not pass verify.\nX.cs(3) SYN001: no\nscenario one failed\nscenario two failed\nscenario three failed");
        var verifySide = TasksApp.RenderSideCore(new TasksWorkspaceState("board", new FlowQuery(), verifyFlow.FlowId, []), [verifyFlow], [], null);
        var bar = Assert.Single(Nodes(verifySide).OfType<InfoBar>(), b => b.Title == "Verify failed");
        Assert.Equal("The unit did not pass verify.\nX.cs(3) SYN001: no\nscenario one failed\n(+2 more)", bar.Message);
    }

    [Fact]
    public async Task FlowsChangedRefreshesTheBoard()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        FakeFlows.Views = [Flow("late", FlowStatus.Running)];

        await tasks.App.OnFlowsChanged(new FlowsChanged(FakeFlows.Views[0].FlowId));

        Assert.Contains("Task late", Strings(tasks.App.Render()));
        var kanban = os.Get<IKanbanBoard>(new NeuronId(LocalBrain, "ui.kanban", TasksApp.KanbanKey));
        Assert.Contains((await kanban.Read()).Cards, c => c.Title == "Task late" && c.ColumnId == "in-progress");
    }

    [Fact]
    public async Task AskInoToChangeWritesUiFocus()
    {
        await using var os = FakeOs();
        FakeFlows.Views = [Flow("unit1", FlowStatus.Completed, type: "flow.build")];
        FakeSynapses.Sources = [new DeployedSource("unit1", "h1", ["weather"], DeployStatus.Deployed, null, T0)];
        var tasks = await os.Install<TasksApp>();

        await tasks.Request("ask-ino", new TasksAskIno(FakeFlows.Views[0].FlowId));

        var point = Assert.Single(FakeFocus.Points);
        Assert.Equal("window:ino", point.Target);
        Assert.Equal(FocusTone.Info, point.Tone);
        Assert.Equal("Change unit weather", point.Reason);
        Assert.Equal(new FocusAction("open", "ino"), point.Action);
    }

    [Fact]
    public async Task TasksShell_RendersSingleRowToolbarFullBleedKanbanAndTableWithZeroCommandBarClutter()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        var engFlow = Flow("eng-1", FlowStatus.Running, type: "flow.engineer");
        FakeFlows.Views = [engFlow];
        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen(FlowId: engFlow.FlowId));

        var view = tasks.App.Render();
        var shell = Assert.IsType<AppShellNode>(view.Root);
        Assert.Equal("tasks-shell", shell.Key);
        Assert.NotNull(shell.Sidebar);
        Assert.NotNull(shell.Inspector);

        // Single <=44px horizontal Row top bar with ui.tabs, filter chips, New Task, and Engineer/Creator deep-links
        var topRow = Assert.IsType<Row>(shell.Toolbar);
        var topNeuronRefs = Nodes(topRow).OfType<NeuronRef>().ToArray();
        Assert.Contains(topNeuronRefs, n => n.Kind == "ui.tabs" && n.NeuronUri == TasksApp.TabsKey);
        Assert.DoesNotContain(Nodes(view).OfType<NeuronRef>(), n => n.Kind == "ui.command-bar");
        Assert.DoesNotContain(Nodes(topRow).OfType<NeuronRef>(), n => n.Kind == "ui.stepper");

        var topButtons = Nodes(topRow).OfType<Button>().ToArray();
        Assert.Contains(topButtons, b => b.Key == "tasks:add" && b.Label == "New Task");
        Assert.Contains(topButtons, b => b.Key == "tasks:filter:all");
        Assert.Contains(topButtons, b => b.Key == "tasks:filter:running");

        var openEng = Assert.Single(topButtons, b => b.Key == "tasks:open-engineer");
        Assert.Equal(AppSignals.OpenApp("engineer", engFlow.FlowId), openEng.Intent);
        var openCreator = Assert.Single(topButtons, b => b.Key == "tasks:open-creator");
        Assert.Equal(AppSignals.OpenApp("creator", engFlow.FlowId), openCreator.Intent);

        // Full-bleed Kanban body: Column with Extents = [1.0] and zero wrapper Card / HeaderBar clutter
        var boardCol = Assert.IsType<Column>(shell.Body);
        Assert.Equal([1.0], boardCol.Extents);
        var kanbanRef = Assert.IsType<NeuronRef>(Assert.Single(boardCol.Children));
        Assert.Equal("ui.kanban", kanbanRef.Kind);
        Assert.Equal(TasksApp.KanbanKey, kanbanRef.NeuronUri);

        // Switch to Table tab -> full-bleed Table body: Column with Extents = [1.0]
        await tasks.Request("view", new TasksView("table"));
        var tableShell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        var tableCol = Assert.IsType<Column>(tableShell.Body);
        Assert.Equal([1.0], tableCol.Extents);
        var tableRef = Assert.IsType<NeuronRef>(Assert.Single(tableCol.Children));
        Assert.Equal("ui.table", tableRef.Kind);
        Assert.Equal(TasksApp.TableKey, tableRef.NeuronUri);
    }

    [Fact]
    public async Task TasksInspector_SurfacesFlowStagesShaBoundConfirmationsAndGitHubIssueTriageDrafts()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        os.Register<FakeGitHubRepository>();

        const string commitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var planId = $"app:engineer:eng-01:{commitSha}";
        await os.Get<IConfirmationRequests>(IConfirmationRequests.Of(LocalBrain)).Request(
            new ConfirmationPlan(planId, ConfirmationAction.Other, false, "Publish Draft PR #42?", "Verified worktree candidate w1", "Publish", "Hold", "intochat/intochat")
            {
                RequiresPerson = true,
            });
        FakeGitHubRepository.Drafts =
        [
            new IssueCommentPreview(
                "draft-issue-42",
                42,
                "Triaged crash in grain timer; assigning p0 label.",
                ["bug", "p0"],
                DateTimeOffset.UtcNow.AddMinutes(10),
                "intochat/intochat"),
        ];
        var waitingFlow = Flow("eng-01", FlowStatus.Waiting, type: "flow.engineer", waiting: WaitReason.Approval) with
        {
            ConfirmationPlanId = planId,
        };
        FakeFlows.Views = [waitingFlow];

        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen(FlowId: waitingFlow.FlowId));

        var shell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        var inspectorNodes = Nodes(shell.Inspector).ToArray();
        var inspectorText = Strings(shell.Inspector).ToArray();

        // 1. Flow stages & Stepper inside collapsible right inspector
        Assert.Contains(inspectorNodes.OfType<NeuronRef>(), n => n.Kind == "ui.stepper" && n.NeuronUri == TasksApp.StepperKey);
        Assert.Contains(inspectorNodes.OfType<JobCardNode>(), j => j.Title == waitingFlow.Title);

        // 2. SHA-bound confirmation details + human Person rule reminder
        var approvalCard = Assert.Single(inspectorNodes.OfType<ApprovalCardNode>(), c => c.Id == planId);
        Assert.Null(approvalCard.Resolve);
        Assert.Contains(approvalCard.Rows, r => r.Label == "Bound SHA" && r.NewValue == commitSha);
        Assert.Contains(inspectorText, s => s.Contains("Person-only rule enforced", StringComparison.Ordinal));

        // 3. Pending GitHub issue triage comment draft discovered from connection/github/main
        var draftCard = Assert.Single(inspectorNodes.OfType<DraftCardNode>(), d => d.Id == "draft-issue-42");
        Assert.Equal("intochat/intochat #42", draftCard.To);
        Assert.Contains("Triaged crash", draftCard.Body, StringComparison.Ordinal);
        Assert.Contains("bug, p0", draftCard.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TasksNavigationLifecycle_SupportsTypedNavigateToDeepLinkTransitionAndDraftGuardedDeparture()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        var flow = Flow("nav-1", FlowStatus.Running);
        FakeFlows.Views = [flow];
        var tasks = await os.Install<TasksApp>();

        await tasks.App.NavigateToAsync(
            new TasksNavArgs(FlowId: flow.FlowId, Tab: "approvals"),
            "app.engineer",
            ViewTransition.SharedAxisX,
            null,
            ct);

        Assert.Equal(flow.FlowId, tasks.App.State.SelectedFlowId);
        Assert.Equal("approvals", tasks.App.State.ActiveTab);
        var shell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        Assert.Equal(ViewTransition.SharedAxisX, shell.Transition);

        // Clean departure succeeds when Draft is empty
        Assert.True(await tasks.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer"), ct));

        // Unsaved draft blocks unforced departure with declarative Rule rejection, while Force: true succeeds
        await Field(tasks, "title", "Unsaved architecture task");
        Assert.False(await tasks.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer", Force: false), ct));
        Assert.NotNull(tasks.App.LastRejection);
        Assert.Contains("unsaved_task_draft", tasks.App.LastRejection!.Rules);

        Assert.True(await tasks.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer", Force: true), ct));
    }

    [Fact]
    public async Task IssueTriageDraftActions_RenderButtonsInInspectorAndApprovalsTabAndApprovePostsCommentWithPersonAuthority()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        os.Register<FakeGitHubRepository>();

        const string previewId = "draft-approve-101";
        FakeGitHubRepository.Drafts =
        [
            new IssueCommentPreview(
                previewId,
                101,
                "Null guard needed on grain activation path.",
                ["bug", "triage"],
                DateTimeOffset.UtcNow.AddMinutes(10),
                "intochat/intochat"),
        ];

        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        // 1. On Board tab: body stays 100% full-bleed ui.kanban ([1.0]) and right inspector renders the 3 triage action buttons + Studio deep-link
        var boardShell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        var boardCol = Assert.IsType<Column>(boardShell.Body);
        Assert.Equal([1.0], boardCol.Extents);
        Assert.Equal("ui.kanban", Assert.IsType<NeuronRef>(Assert.Single(boardCol.Children)).Kind);

        var inspectorButtons = Nodes(boardShell.Inspector).OfType<Button>().ToArray();
        Assert.Contains(inspectorButtons, b => b.Key == $"tasks:approve-issue-draft:{previewId}");
        Assert.Contains(inspectorButtons, b => b.Key == $"tasks:dismiss-issue-draft:{previewId}");
        Assert.Contains(inspectorButtons, b => b.Key == $"tasks:escalate-issue:{previewId}");
        var openStudioBtn = Assert.Single(inspectorButtons, b => b.Key == $"tasks:open-engineer-issue:{previewId}");
        Assert.Equal(TasksApp.BuildEngineerIssueIntent("intochat/intochat", 101), openStudioBtn.Intent);

        // 2. On Approvals tab: main center stage renders the canonical action buttons with zero duplicate keys across the shell
        await tasks.Request("view", new TasksView("approvals"));
        var approvalsShell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        var approvalsBodyButtons = Nodes(approvalsShell.Body).OfType<Button>().ToArray();
        Assert.Contains(approvalsBodyButtons, b => b.Key == $"tasks:approve-issue-draft:{previewId}");
        Assert.Contains(approvalsBodyButtons, b => b.Key == $"tasks:dismiss-issue-draft:{previewId}");
        Assert.Contains(approvalsBodyButtons, b => b.Key == $"tasks:escalate-issue:{previewId}");
        Assert.Contains(approvalsBodyButtons, b => b.Key == $"tasks:open-engineer-issue:{previewId}");

        var liveGraph = AggregateExplorer<TasksWorkspaceState>.ExploreScreen(tasks.App, maxStates: 32).EnsureHealthy();
        Assert.Empty(liveGraph.Violations);

        // 3. Dispatching TasksApproveIssueDraft posts comment + labels via IRepository.CommentOnIssue under Person provenance and clears the draft
        await tasks.Request(
            "approve-issue-draft",
            new TasksApproveIssueDraft("connection/github/main", 101, previewId));

        var posted = Assert.Single(FakeGitHubRepository.CommentsPosted);
        Assert.Equal(previewId, posted.Request.PreviewId);
        Assert.Equal($"tasks-approve:{previewId}", posted.Request.RequestId);
        Assert.Equal(101, posted.Request.IssueNumber);
        Assert.IsType<DigitalBrain.Access.Actor.Person>(posted.CallerActor);

        Assert.Empty(FakeGitHubRepository.Drafts);
        Assert.DoesNotContain(Nodes(tasks.App.Render()).OfType<DraftCardNode>(), d => d.Id.Contains(previewId, StringComparison.Ordinal));
        Assert.Contains("Approved & posted triage comment on issue #101 (labels: bug, triage).", tasks.App.State.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueTriageDraftDismiss_RemovesPendingDraftWithoutPostingToGitHub()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        os.Register<FakeGitHubRepository>();

        const string previewId = "draft-dismiss-102";
        FakeGitHubRepository.Drafts =
        [
            new IssueCommentPreview(
                previewId,
                102,
                "Duplicate report of #88.",
                ["duplicate"],
                DateTimeOffset.UtcNow.AddMinutes(10),
                "intochat/intochat"),
        ];

        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());
        Assert.Contains(Nodes(tasks.App.Render()).OfType<Button>(), b => b.Key == $"tasks:dismiss-issue-draft:{previewId}");

        await tasks.Request(
            "dismiss-issue-draft",
            new TasksDismissIssueDraft("connection/github/main", previewId));

        Assert.Equal([previewId], FakeGitHubRepository.DismissedPreviewIds);
        Assert.Empty(FakeGitHubRepository.CommentsPosted);
        Assert.Empty(FakeGitHubRepository.Drafts);
        Assert.DoesNotContain(Nodes(tasks.App.Render()).OfType<DraftCardNode>(), d => d.Id.Contains(previewId, StringComparison.Ordinal));
        Assert.Contains($"Dismissed issue triage draft '{previewId}'.", tasks.App.State.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueTriageEscalate_ReadsIssueStartsEngineerRunLinksStudioIntentAndPreservesFullBleedAndExplorerHealth()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", LocalBrain));
        await using var os = FakeOsWithUi();
        os.Register<FakeGitHubRepository>();

        const string previewId = "draft-escalate-103";
        FakeGitHubRepository.Drafts =
        [
            new IssueCommentPreview(
                previewId,
                103,
                "Race condition in timer callback.",
                ["bug", "p1"],
                DateTimeOffset.UtcNow.AddMinutes(10),
                "intochat/intochat"),
        ];

        var tasks = await os.Install<TasksApp>();
        await tasks.Request("open", new TasksOpen());

        await tasks.Request(
            "escalate-issue",
            new TasksEscalateIssue("connection/github/main", "intochat/intochat", 103, previewId));

        Assert.Equal([103], FakeGitHubRepository.ReadIssueNumbers);
        var (delegation, startedFlowId) = Assert.Single(FakeFlows.EngineerRuns);
        Assert.Equal("intochat/intochat", delegation.RepoSlug);
        Assert.True(delegation.RequirePlanRatification);
        Assert.Contains("#103", delegation.Goal, StringComparison.Ordinal);
        Assert.Contains("Scheduler race when grain deactivates", delegation.Goal, StringComparison.Ordinal);
        Assert.Contains(IssueRead.UntrustedEvidenceHeader, delegation.Goal, StringComparison.Ordinal);

        Assert.Equal(startedFlowId, tasks.App.State.SelectedFlowId);
        Assert.Equal(startedFlowId, tasks.App.State.FindEscalatedFlowId(previewId, "intochat/intochat", 103));

        var renderedButtons = Nodes(tasks.App.Render()).OfType<Button>().ToArray();
        var openStudioIssueBtn = Assert.Single(renderedButtons, b => b.Key == $"tasks:open-engineer-issue:{previewId}");
        Assert.Equal(
            AppSignals.OpenApp(
                "engineer",
                new StudioNavArgs(
                    TargetId: "intochat/intochat#103",
                    Lens: "plan",
                    FlowId: startedFlowId)),
            openStudioIssueBtn.Intent);

        // Verify Board and Table remain 100% full-bleed ([1.0]) and AggregateExplorer stays 0-violation healthy
        var boardShell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        Assert.Equal([1.0], Assert.IsType<Column>(boardShell.Body).Extents);

        await tasks.Request("view", new TasksView("table"));
        var tableShell = Assert.IsType<AppShellNode>(tasks.App.Render().Root);
        Assert.Equal([1.0], Assert.IsType<Column>(tableShell.Body).Extents);

        var graph = AggregateExplorer<TasksWorkspaceState>.ExploreScreen(tasks.App, maxStates: 32).EnsureHealthy();
        Assert.Empty(graph.Violations);
    }

    private static async Task Field(InstalledApp<TasksApp> tasks, string field, string value)
    {
        FakeField.Values[$"apps/tasks/{field}"] = value;
        await tasks.App.OnField(field, new FieldChanged("local/apps/tasks", field, 1));
    }

    private static TestOS FakeOs()
    {
        FakeFlows.Reset();
        FakeGitHubRepository.Reset();
        FakeFocus.Points.Clear();
        FakeSynapses.Sources = [];
        FakeSynapses.SidecarFiles = [];
        FakeSynapses.Files = [];
        var os = TestOS.Local();
        os.Register<FakeFlows>();
        os.Register<FakeEngineerRuns>();
        os.Register<FakeApprovals>();
        os.Register<FakeFocus>();
        os.Register<FakeSynapses>();
        os.Register<FakeField>();
        return os;
    }

    private static TestOS FakeOsWithUi()
    {
        var os = FakeOs();
        os.Register(typeof(UiModule).Assembly);
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
        Node or View or JobStepNode => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Strings(p.GetValue(value))),
        _ => [],
    };

    [Neuron("flows")]
    private sealed class FakeFlows : Neuron, IFlows
    {
        public static List<(string Type, string Title, string InnerJson, string Id)> Started { get; } = [];
        public static List<(EngineerDelegationRequest Request, string FlowId)> EngineerRuns { get; } = [];
        public static FlowView[] Views { get; set; } = [];
        public static List<string> Cancels { get; } = [];
        public static List<(string FlowId, FlowRelation Relation)> Relations { get; } = [];
        public static string? CancelRefusal { get; set; }

        public static void Reset()
        {
            Started.Clear();
            EngineerRuns.Clear();
            Cancels.Clear();
            Relations.Clear();
            Views = [];
            CancelRefusal = null;
        }

        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult(Views);
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult(Views.FirstOrDefault(v => v.FlowId == flowId));

        public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
        {
            var id = $"local/{type}/{Started.Count + 1}";
            Started.Add((type, title, innerJson, id));
            return Task.FromResult(id);
        }

        public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;

        public Task Cancel(string flowId, string reason, CancellationToken ct = default)
        {
            if (CancelRefusal is { } message)
            {
                throw new DigitalBrain.Rules.RuleViolationException(new DigitalBrain.Rules.Rejected("Cancel", ["apps.flows-person-cancels-approval-wait"], [message]));
            }

            Cancels.Add(flowId);
            return Task.CompletedTask;
        }

        public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default)
        {
            Relations.Add((flowId, relation));
            return Task.CompletedTask;
        }
    }

    [Neuron("engineer.runs")]
    private sealed class FakeEngineerRuns : Neuron, IEngineerRuns
    {
        public Task<EngineerRunSummary> StartEngineerRun(EngineerDelegationRequest request, CancellationToken ct = default)
        {
            var runId = $"eng-run-{FakeFlows.EngineerRuns.Count + 1}";
            var flowId = $"local/{IEngineerRuns.EngineerFlow}/{runId}";
            FakeFlows.EngineerRuns.Add((request, flowId));
            return Task.FromResult(new EngineerRunSummary(
                Ok: true,
                FlowId: flowId,
                RunId: runId,
                RepoSlug: request.RepoSlug,
                Goal: request.Goal,
                Backend: request.Backend,
                ParallelInstances: request.ParallelInstances,
                Status: FlowStatus.Running,
                CurrentStage: "brainstorm-and-plan",
                CompletedStages: [],
                ConfirmationPlanId: null,
                TraceId: $"trace-{runId}",
                WinningCommitSha: null,
                PushedRef: null,
                PullRequestUrl: null,
                EngineerAppUri: $"os://window/open?app=engineer&input={flowId}",
                TasksBoardUri: $"os://window/open?app=tasks&input={flowId}",
                Plan: null,
                Instances: [],
                DiagnosticsAndTraces: [],
                SummaryText: $"Started {flowId}"));
        }

        public Task<EngineerRunSummary> InspectEngineerRun(string? flowId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EngineerRunSummary> SelectEngineerCandidate(string flowId, int candidateIndex, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EngineerRunSummary> RatifyEngineerPlan(string flowId, string[]? approvedCleanupTargets = null, EngineerTaskSlice[]? slices = null, CancellationToken ct = default) => throw new NotSupportedException();
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

    [Neuron("approvals")]
    private sealed class FakeApprovals : Neuron, IApprovals
    {
        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Approval>>([]);
    }

    [Neuron("ino.focus")]
    private sealed class FakeFocus : Neuron, IFocus
    {
        public static List<Focus> Points { get; } = [];

        public Task<InoToolResult> Point(string target, FocusTone tone, string reason, FocusAction? action = null)
        {
            Points.Add(new Focus(target, tone, reason, action));
            return Task.FromResult(new InoToolResult("Pointed."));
        }

        public Task<InoToolResult> Clear() => Task.FromResult(new InoToolResult("Cleared."));
        public Task<InoToolResult> Show(string title, string node) => Task.FromResult(new InoToolResult("Shown."));
    }

    [Neuron("ino.synapses")]
    private sealed class FakeSynapses : Neuron, ISynapseStudio
    {
        public static DeployedSource[] Sources { get; set; } = [];
        public static Dictionary<string, string> SidecarFiles { get; set; } = [];
        public static Dictionary<string, string> Files { get; set; } = [];

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult(""));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => Task.FromResult(new UnitDeployResult(true, "", DeployStatus.Deployed, null, new Dictionary<string, InstallOutcome>()));
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(SidecarFiles);
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(Files);
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult($"{alias} has no recorded failures.", Ok: true));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
        public Task<DeployedSource[]> List() => Task.FromResult(Sources);
    }

    [Neuron("github")]
    private sealed class FakeGitHubRepository : Neuron, IRepository
    {
        public static IssueCommentPreview[] Drafts { get; set; } = [];
        public static List<(CommentOnIssueRequest Request, DigitalBrain.Access.Actor? CallerActor)> CommentsPosted { get; } = [];
        public static List<string> DismissedPreviewIds { get; } = [];
        public static List<int> ReadIssueNumbers { get; } = [];

        public static void Reset()
        {
            Drafts = [];
            CommentsPosted.Clear();
            DismissedPreviewIds.Clear();
            ReadIssueNumbers.Clear();
        }

        public Task<RepositoryView> Refresh(RefreshRepository request) => throw new NotSupportedException();
        public Task<RepositoryView> Read() => throw new NotSupportedException();
        public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PullRequestsRead> ReadPullRequests() => throw new NotSupportedException();
        public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PushBranch(PushBranchRequest request) => throw new NotSupportedException();
        public Task<PullRequestView> CreatePullRequest(CreatePullRequest request) => throw new NotSupportedException();
        public Task ReplyToReviewComment(ReplyReviewComment request) => throw new NotSupportedException();
        public Task MergePullRequest(MergePullRequest request) => throw new NotSupportedException();

        public Task<IssueRead> ReadIssue(ReadIssue query, CancellationToken cancellationToken = default)
        {
            ReadIssueNumbers.Add(query.Number);
            var snapshot = new IssueSnapshot(
                Number: query.Number,
                Title: "Scheduler race when grain deactivates",
                Body: "Stack trace in grain timer callback.",
                Url: $"https://github.com/intochat/intochat/issues/{query.Number}",
                IsOpen: true,
                Author: "octocat",
                Labels: ["bug"],
                CommentCount: 2,
                ObservedAt: DateTimeOffset.UtcNow,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                Revision: $"rev-{query.Number}",
                RepositoryId: 4242);
            var fenced = IssueRead.FenceUntrustedBody("intochat", "intochat", 4242, snapshot);
            return Task.FromResult(new IssueRead(snapshot with { Body = fenced }, fenced));
        }

        public Task<IssueCommentResult> CommentOnIssue(CommentOnIssueRequest request, CancellationToken cancellationToken = default)
        {
            CommentsPosted.Add((request, Context.Caller?.Actor));
            var matched = !string.IsNullOrWhiteSpace(request.PreviewId)
                ? Drafts.FirstOrDefault(d => string.Equals(d.PreviewId, request.PreviewId, StringComparison.Ordinal))
                : null;
            var issueNum = request.IssueNumber > 0 ? request.IssueNumber : (matched?.IssueNumber ?? 1);
            var labels = request.Labels ?? matched?.SuggestedLabels ?? [];
            if (matched is not null)
            {
                Drafts = Drafts.Where(d => !string.Equals(d.PreviewId, matched.PreviewId, StringComparison.Ordinal)).ToArray();
            }

            return Task.FromResult(new IssueCommentResult(
                issueNum,
                9001,
                $"https://github.com/intochat/intochat/issues/{issueNum}#issuecomment-9001",
                labels,
                request.RequestId ?? "req-1",
                Deduplicated: false));
        }

        public Task<bool> DismissIssueDraft(string previewId, CancellationToken cancellationToken = default)
        {
            DismissedPreviewIds.Add(previewId);
            var next = Drafts.Where(d => !string.Equals(d.PreviewId, previewId, StringComparison.Ordinal)).ToArray();
            var removed = next.Length != Drafts.Length;
            Drafts = next;
            return Task.FromResult(removed);
        }

        public Task<IssueCommentPreview[]> ReadPendingIssueDrafts() => Task.FromResult(Drafts);
    }
}
