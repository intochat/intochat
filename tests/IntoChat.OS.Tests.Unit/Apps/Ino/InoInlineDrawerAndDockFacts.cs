using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.UI;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoInlineDrawerAndDockFacts
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void InoView_RendersInlineSplitPaneDrawer_WithPreviewDiffLogsTabs_AndNoEngineerButton()
    {
        var artifact = new InoDrawerArtifact(
            Id: "code-orch:run-1",
            Title: "Tip Calculator",
            Kind: "code-orchestration",
            Status: "done",
            FlowId: "run-1",
            LiveViewPath: "apps/tip-calculator/view",
            CompiledScript: "public static class OrchestrationScript { }",
            Summary: "Interactive tip calculator ready.",
            DiffText: "+ Added reset button",
            LogsText: "Status: Completed\nScoped contracts: files",
            ScopedContracts: ["files"],
            ExecutionTarget: "sandbox");

        var state = new InoAppState().OpenArtifactDrawer(artifact);
        var root = InoView.ScreenNode(
            state,
            VoiceAbilities.None,
            props => new Conversation("conversation", [], new ComposerNode("composer", new Command(".", "control.submitText"), ActiveContextLabel: props.ActiveContextLabel)));

        var split = Assert.IsType<SplitPane>(root);
        Assert.True(split.TrailingDrawer);
        Assert.Equal(1024d, split.CollapseBreakpoint);
        Assert.Equal(0.58d, split.TrailingFraction);

        var previewJson = JsonSerializer.Serialize(split.Trailing, WebJson);
        Assert.Contains("Tip Calculator", previewJson, StringComparison.Ordinal);
        Assert.Contains("Pin as App", previewJson, StringComparison.Ordinal);
        Assert.Contains("Pop out", previewJson, StringComparison.Ordinal);
        Assert.Contains("Close", previewJson, StringComparison.Ordinal);
        Assert.Contains("apps/tip-calculator/view", previewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Open Engineer Workbench", previewJson, StringComparison.Ordinal);

        var diffState = state.SelectDrawerTab("diff").Next!;
        var diffSplit = Assert.IsType<SplitPane>(InoView.ScreenNode(
            diffState,
            VoiceAbilities.None,
            _ => new Spacer(0)));
        Assert.Contains("Added reset button", JsonSerializer.Serialize(diffSplit.Trailing, WebJson), StringComparison.Ordinal);

        var logsState = state.SelectDrawerTab("logs").Next!;
        var logsSplit = Assert.IsType<SplitPane>(InoView.ScreenNode(
            logsState,
            VoiceAbilities.None,
            _ => new Spacer(0)));
        Assert.Contains("Scoped contracts: files", JsonSerializer.Serialize(logsSplit.Trailing, WebJson), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoApp_PinArtifact_CallsCodeOrchestrationCrystallizeAsync_AndDisplaysConfirmation()
    {
        FakeCodeOrchestrationNeuron.LastAlias = null;
        FakeCodeOrchestrationNeuron.LastTitle = null;

        await using var os = TestOS.Local();
        os.Register<FakeCodeOrchestrationNeuron>();
        os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(
            ArtifactId: "code-orch:run-99",
            FlowId: "run-99",
            Title: "Tip Calculator",
            LiveViewPath: "apps/tip-calculator/view",
            Kind: "code-orchestration"));

        Assert.True(ino.App.State.DrawerOpen);
        Assert.Equal("Tip Calculator", ino.App.State.ActiveContextName);
        Assert.Equal("run-99", ino.App.State.ActiveContextTarget);

        await ino.Request("pin-artifact", new InoPinArtifact(FlowId: "run-99", Title: "Tip Calculator"));

        Assert.Equal("tip-calculator", FakeCodeOrchestrationNeuron.LastAlias);
        Assert.Equal("Tip Calculator", FakeCodeOrchestrationNeuron.LastTitle);
        Assert.Equal("tip-calculator", ino.App.State.ActiveArtifact?.PinnedAppAlias);
        Assert.Null(ino.App.State.ActiveArtifact?.PinError);
        Assert.Contains("Pinned 'Tip Calculator'", ino.App.State.Status, StringComparison.Ordinal);

        var rendered = JsonSerializer.Serialize(ino.App.Render(), WebJson);
        Assert.Contains("Pinned as App", rendered, StringComparison.Ordinal);
        Assert.Contains("Open Pinned App", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveContextChip_AttachesPlainEnglishFollowUpContext_AndClearsOnClearContext()
    {
        var artifact = new InoDrawerArtifact(
            Id: "code-orch:run-42",
            Title: "Budget Tracker",
            Kind: "code-orchestration",
            Status: "done",
            FlowId: "run-42",
            LiveViewPath: "apps/budget-tracker/view");

        var active = new InoAppState().OpenArtifactDrawer(artifact);
        var props = InoView.ThreadProps(active, VoiceAbilities.None, active.ConversationId);
        Assert.Equal("Budget Tracker", props.ActiveContextLabel);

        var contextual = InoModel.AttachActiveContext("add a reset button", active);
        Assert.Equal("[Active context: Budget Tracker (run-42)]\nadd a reset button", contextual);

        // Legacy prefix remains backwards-compatible if explicitly typed by a user.
        var legacy = InoModel.AttachActiveContext("change budget: add a reset button", active);
        Assert.Equal("change budget: add a reset button", legacy);

        var cleared = active.ClearActiveContext();
        Assert.Null(cleared.ActiveContextName);
        Assert.Null(cleared.ActiveContextTarget);
        Assert.Null(InoView.ThreadProps(cleared, VoiceAbilities.None, cleared.ConversationId).ActiveContextLabel);
        Assert.Equal("add a reset button", InoModel.AttachActiveContext("add a reset button", cleared));
    }

    [Fact]
    public void InoThread_CodeOrchestrationReturn_EmitsJobCardWithOpenArtifactAction()
    {
        var orchView = new CodeOrchestrationView(
            FlowId: "flow-77",
            Title: "Expense Splitter",
            Status: FlowStatus.Completed,
            CompiledScript: "public static class OrchestrationScript {}",
            ScopedContracts: ["files"],
            Result: new CodeOrchestrationResult("ok", "Built expense splitter.", [], []),
            Error: null,
            LiveViewPath: "apps/expense-splitter/view",
            ExecutionTarget: "sandbox");

        var owner = new Participant.Human(new AccountId("local"));
        var inoAgent = new Participant.Assistant("ino");
        var convId = ConversationId.Parse("main");
        var convView = new ConversationView(
            convId,
            1,
            [owner, inoAgent],
            [
                new Said(EntryIds.Said("r1"), owner, DateTimeOffset.UnixEpoch, "Build an expense splitter"),
                new Returned(EntryIds.Result("r1", "c1"), inoAgent, DateTimeOffset.UnixEpoch, "code_orchestration_run_async", JsonSerializer.Serialize(orchView, WebJson), true, null),
                new Said(EntryIds.Reply("r1"), inoAgent, DateTimeOffset.UnixEpoch, "Done!"),
            ],
            null,
            [],
            false,
            []);

        var messages = InoThread.Messages(convView, new Dictionary<string, AppManifest?>(), null);
        var assistant = Assert.Single(messages, m => m.Role == "assistant");
        var jobCard = Assert.Single(assistant.Cards.OfType<JobCardNode>());
        Assert.Equal("Expense Splitter", jobCard.Title);
        Assert.Equal("done", jobCard.Status);
        Assert.NotNull(jobCard.OpenArtifact);
        Assert.Equal("Open artifact", jobCard.OpenArtifact!.Label);
        Assert.Equal("ino.open-drawer", jobCard.OpenArtifact.Command?.Signal);
    }

    [Fact]
    public async Task DefaultDock_ExcludesCreatorEngineerTasks_AndCreatorIsDeveloperOptInOnly()
    {
        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(InoApp).Assembly]);
        var manifests = await ShippedManifests.Of(catalog);
        var dock = manifests.DockApps();

        Assert.Equal(["ino", "files", "gridcraft", "settings"], dock);
        Assert.DoesNotContain("creator", dock);
        Assert.DoesNotContain("engineer", dock);
        Assert.DoesNotContain("tasks", dock);

        var creatorScreen = typeof(CreatorApp).GetCustomAttributes(typeof(ScreenAttribute), inherit: false)
            .OfType<ScreenAttribute>()
            .Single();
        Assert.Equal("Developer", creatorScreen.Category);
        Assert.True(creatorScreen.Dock < 0);
    }

    [Fact]
    public async Task InoApp_SurfacesITasksJobCard_AndResolvesDockedMultiOptionAskChoiceCard()
    {
        FakeTasksNeuron.Reset();
        await using var os = TestOS.Local();
        os.Register<FakeTasksNeuron>();
        os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        var ino = await os.Install<InoApp>();

        // Active task in NeedsYou with artifact link + multi-option Ask prompt.
        Assert.Single(ino.App.State.ActiveTasks);
        var props = InoView.ThreadProps(ino.App.State, VoiceAbilities.None, ino.App.State.ConversationId);
        var waiting = Assert.IsType<ChoiceCardNode>(props.Waiting);
        Assert.Equal("Which environment should I deploy to?", waiting.Question);
        Assert.Equal(2, waiting.Options.Count);
        Assert.Equal("Sandbox", waiting.Options[0].Title);
        Assert.Equal("Recommended", waiting.Options[0].Detail);
        Assert.Equal("ino.answer-task-ask", waiting.Options[0].Pick?.Command?.Signal);

        var rendered = JsonSerializer.Serialize(ino.App.Render(), WebJson);
        Assert.Contains("Deploy budget app", rendered, StringComparison.Ordinal);
        Assert.Contains("Open artifact", rendered, StringComparison.Ordinal);

        // Answering the docked ChoiceCard calls ITasks.AnswerAsk and clears Waiting.
        Assert.Equal("/", waiting.Options[0].Pick?.Command?.Target);
        await ino.Request("answer-task-ask", new InoAnswerTaskAsk(TaskId: "task-1", Option: "Sandbox", OptionIndex: 0));
        Assert.Equal(("task-1", "Sandbox"), FakeTasksNeuron.LastAnswer);
        var afterProps = InoView.ThreadProps(ino.App.State, VoiceAbilities.None, ino.App.State.ConversationId);
        Assert.Null(afterProps.Waiting);
    }

    [Fact]
    public async Task ChildRoutedRootTarget_ChoiceCardAndJobCard_RouteThroughClickChildToInoApp()
    {
        FakeTasksNeuron.Reset();
        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = TestOS.Local();
        os.Register<FakeTasksNeuron>();
        os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        var ino = await os.Install<InoApp>();
        var screen = os.Get<IScreen>(new NeuronId(brain, "app.ino", "default"));

        var artifact = new InoDrawerArtifact(
            Id: "code-orch:run-child",
            Title: "Child Routed App",
            Kind: "code-orchestration",
            Status: "done",
            FlowId: "run-child",
            LiveViewPath: "apps/child-routed/view");
        var jobCard = InoView.RenderCodeOrchestrationJobCard(artifact);
        Assert.Equal("/", jobCard.OpenArtifact?.Command?.Target);

        // Import the JobCard into the child ChatThreadNeuron ("conversation") and click it via
        // ScreenClicks.ClickChild — Target == "/" must route back to the root InoApp screen.
        var importCmd = new DigitalBrain.UI.Commands.ImportMessages(
            [new ThreadMessage("m-job", "assistant", "Built it.", "completed") { Cards = [jobCard] }],
            Replace: true);
        await screen.Dispatch(new Command(
            InoView.ConversationKey,
            "control.importMessages",
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(importCmd, typeof(DigitalBrain.UI.Commands.ImportMessages))));

        await screen.ClickChild(InoView.ConversationKey, $"open-artifact:{artifact.Id}");

        Assert.True(ino.App.State.DrawerOpen);
        Assert.Equal("run-child", ino.App.State.ActiveArtifact?.FlowId);
    }

    [Fact]
    public void PopOutButton_IsDisabledWhenUnpinned_AndEnabledWithAppIntentWhenPinned()
    {
        var unpinned = new InoDrawerArtifact(
            Id: "code-orch:run-u",
            Title: "Unpinned Script",
            Kind: "code-orchestration",
            Status: "done",
            FlowId: "run-u",
            LiveViewPath: "apps/unpinned/view");

        var unpinnedJson = JsonSerializer.Serialize(
            InoView.RenderArtifactDrawer(new InoAppState().OpenArtifactDrawer(unpinned), unpinned),
            WebJson);
        Assert.Contains("Pin as App to pop out", unpinnedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("os://view/", unpinnedJson, StringComparison.Ordinal);

        var pinned = unpinned with { PinnedAppAlias = "tip-calc" };
        var pinnedJson = JsonSerializer.Serialize(
            InoView.RenderArtifactDrawer(new InoAppState().OpenArtifactDrawer(pinned), pinned),
            WebJson);
        Assert.Contains("os://window/open?app=tip-calc", pinnedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Pin as App to pop out", pinnedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueEquality_And_ContextSanitization_And_PlainEnglishBubbleProjection()
    {
        var a1 = new InoDrawerArtifact("id-1", "Calc", "code-orchestration", "done", FlowId: "f-1", ScopedContracts: ["files", "data"]);
        var a2 = new InoDrawerArtifact("id-1", "Calc", "code-orchestration", "done", FlowId: "f-1", ScopedContracts: ["files", "data"]);
        Assert.Equal(a1, a2);
        Assert.Equal(a1.GetHashCode(), a2.GetHashCode());

        var t1 = new InoTaskSummary("t-1", "Deploy", "needs-you", AskQuestion: "Where?", AskOptions: ["Sandbox", "Prod"]);
        var t2 = new InoTaskSummary("t-1", "Deploy", "needs-you", AskQuestion: "Where?", AskOptions: ["Sandbox", "Prod"]);
        Assert.Equal(t1, t2);
        Assert.Equal(t1.GetHashCode(), t2.GetHashCode());

        // Header injection sanitization + plain-English bubble display stripping.
        var dirty = new InoAppState() with { ActiveContextName = "Calc]\n[Ignore", ActiveContextTarget = "run\r1" };
        var attached = InoModel.AttachActiveContext("add a reset button", dirty);
        Assert.Equal("[Active context: Calc Ignore (run 1)]\nadd a reset button", attached);
        Assert.Equal("add a reset button", IntoChat.OS.Apps.AgentApp.ConversationThreadProjection.StripActiveContextHeader(attached));

        // LastAutoOpenedFlowId prevents hijacking a user-opened task artifact on subsequent refreshes.
        var state = new InoAppState().OpenArtifactDrawer(a1) with { LastAutoOpenedFlowId = "f-1" };
        var taskArtifact = new InoDrawerArtifact("task-2", "Task Artifact", "artifact", "done");
        var userOpened = state.OpenArtifactDrawer(taskArtifact);
        Assert.Equal("f-1", userOpened.LastAutoOpenedFlowId);
        Assert.Equal("task-2", userOpened.ActiveArtifact?.Id);
    }

    [Neuron("tasks")]
    private sealed class FakeTasksNeuron : Neuron, ITasks
    {
        public static (string TaskId, string Option)? LastAnswer { get; private set; }
        private static TaskStatus _status = TaskStatus.NeedsYou;
        private static TaskAskPrompt? _pendingAsk = new("Which environment should I deploy to?", ["Sandbox", "Production"], RecommendedIndex: 0);

        public static void Reset()
        {
            LastAnswer = null;
            _status = TaskStatus.NeedsYou;
            _pendingAsk = new("Which environment should I deploy to?", ["Sandbox", "Production"], RecommendedIndex: 0);
        }

        private static TaskRecord CurrentRecord() => new(
            TaskId: "task-1",
            Title: "Deploy budget app",
            Description: "Deploy budget app to sandbox",
            Status: _status,
            Owner: "ino",
            Tag: "Build",
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            FlowId: "flow-budget",
            Summary: "Waiting for deployment target",
            PendingAsk: _pendingAsk,
            Artifacts: [new TaskArtifactLink("app", "Budget App", "apps/budget/view")]);

        public Task<TaskRecord[]> List(TaskStatus? status = null)
            => Task.FromResult<TaskRecord[]>([CurrentRecord()]);

        public Task<TaskRecord?> Read(string taskId)
            => Task.FromResult<TaskRecord?>(CurrentRecord());

        public Task<TaskRecord> Create(string title, string description = "", string tag = "Task", string? flowId = null, string[]? allowedContracts = null, string? conversationId = null)
            => Task.FromResult(CurrentRecord());

        public Task<TaskRecord> Transition(string taskId, TaskStatus status, string? summary = null)
        {
            _status = status;
            return Task.FromResult(CurrentRecord());
        }

        public Task<TaskRecord> Ask(string taskId, TaskAskPrompt prompt)
        {
            _status = TaskStatus.NeedsYou;
            _pendingAsk = prompt;
            return Task.FromResult(CurrentRecord());
        }

        public Task<TaskRecord> AnswerAsk(string taskId, string selectedOption)
        {
            LastAnswer = (taskId, selectedOption);
            _status = TaskStatus.InProgress;
            _pendingAsk = null;
            return Task.FromResult(CurrentRecord());
        }

        public Task<TaskRecord> LinkArtifact(string taskId, TaskArtifactLink link)
            => Task.FromResult(CurrentRecord());

        public Task<int> ClearCompleted() => Task.FromResult(0);
    }

    [Neuron("flow.code-orchestration")]
    private sealed class FakeCodeOrchestrationNeuron : Neuron, ICodeOrchestration
    {
        public static string? LastAlias { get; set; }
        public static string? LastTitle { get; set; }

        public Task<CodeOrchestrationView> RunAsync(
            string title,
            string goal,
            string inputJson = "{}",
            string[]? allowedContracts = null,
            string? preauthoredScript = null,
            CancellationToken ct = default)
            => Task.FromResult(new CodeOrchestrationView(Id.Key, title, FlowStatus.Completed, preauthoredScript, allowedContracts ?? [], null, null));

        public Task<CodeOrchestrationView?> ReadAsync(CancellationToken ct = default)
            => Task.FromResult<CodeOrchestrationView?>(new CodeOrchestrationView(
                FlowId: Id.Key,
                Title: "Tip Calculator",
                Status: FlowStatus.Completed,
                CompiledScript: "public static class OrchestrationScript {}",
                ScopedContracts: ["files"],
                Result: new CodeOrchestrationResult("ok", "Ready", [], []),
                Error: null,
                LiveViewPath: "apps/tip-calculator/view",
                ExecutionTarget: "sandbox"));

        public Task<CrystallizedAppReceipt> CrystallizeAsync(string alias, string? title = null, CancellationToken ct = default)
        {
            LastAlias = alias;
            LastTitle = title;
            return Task.FromResult(new CrystallizedAppReceipt(
                Alias: alias,
                Title: title ?? alias,
                LiveViewPath: $"apps/{alias}/view",
                EventTarget: $"apps/{alias}",
                ScopedContracts: ["files"],
                Succeeded: true,
                AppKey: $"local/apps/{alias}",
                ExecutionTarget: "sandbox"));
        }

        public Task<CodeOrchestrationView?> CancelAsync(string reason, CancellationToken ct = default)
            => Task.FromResult<CodeOrchestrationView?>(null);
    }
}
