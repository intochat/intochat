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
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.UI;

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

        Assert.Equal(["ino", "friends", "files", "gridcraft", "settings"], dock);
        Assert.DoesNotContain("creator", dock);
        Assert.DoesNotContain("engineer", dock);
        Assert.DoesNotContain("tasks", dock);

        var creatorScreen = typeof(CreatorApp).GetCustomAttributes(typeof(ScreenAttribute), inherit: false)
            .OfType<ScreenAttribute>()
            .Single();
        Assert.Equal("Developer", creatorScreen.Category);
        Assert.True(creatorScreen.Dock < 0);
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
