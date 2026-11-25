using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.UI.BreadcrumbBar;
using IntoChat.OS.Modules.UI.Chart;
using IntoChat.OS.Modules.UI.Chart.Signals;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.ContextMenu;
using IntoChat.OS.Modules.UI.Dialog;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.GridView;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.MarkdownViewer;
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TreeView;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.UI;

public sealed class UiModuleOnTestOS : ModuleOnTestOSFacts<UiModule>
{
    private static readonly BrainId Brain = new("owner");

    [Fact]
    public async Task TwoChartsMaintainIndependentStateStreamingAndQueryBinding()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var streamChart = os.Get<IChart>(new NeuronId(Brain, "ui.chart", "stream"));
        var queryChart = os.Get<IChart>(new NeuronId(Brain, "ui.chart", "revenue"));

        await streamChart.Render(new ChartSpec("Live Stream", ChartKinds.Line, [new ChartSeries("Throughput", [new ChartPoint("T1", 10, "e1")])], "Streaming demo"));
        await streamChart.Append(new ChartPoint("T2", 25, "e2"));
        await streamChart.Append(new ChartPoint("T2-dup", 99, "e2")); // duplicate EventId ignored

        var sourceAddress = new NeuronId(Brain, "ui.sample-chart-source", "default").ToString();
        await queryChart.Bind(sourceAddress, new ChartQuery(Filter: "q1"));
        await queryChart.SetKind(ChartKinds.Area);
        await queryChart.SelectPoint("Feb");

        var s1 = await streamChart.Read();
        var s2 = await queryChart.Read();

        Assert.Equal(ChartKinds.Line, s1.Kind);
        Assert.Equal(2, s1.Series[0].Points.Length);
        Assert.Equal("T2", s1.Series[0].Points[1].Label);

        Assert.Equal(ChartKinds.Area, s2.Kind);
        Assert.Equal(2, s2.Series.Length);
        Assert.Equal(3, s2.Series[0].Points.Length); // Jan, Feb, Mar for Q1
        Assert.NotNull(s2.SelectedPoint);
        Assert.Equal("Feb", s2.SelectedPoint!.Label);
        Assert.Equal(82, s2.SelectedPoint.Value);
    }

    [Fact]
    public async Task ChartRollingWindowCapsAt200PointsAndEmitsSignals()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var chart = os.Get<IChart>(new NeuronId(Brain, "ui.chart", "rolling"));
        for (var i = 1; i <= 205; i++)
        {
            await chart.Append(new ChartPoint($"P{i}", i, $"evt-{i}"));
        }

        var state = await chart.Read();
        Assert.Equal(205, state.Version);
        Assert.Equal(ChartState.MaxPointsPerSeries, state.Series[0].Points.Length);
        Assert.Equal("P6", state.Series[0].Points[0].Label);
        Assert.Equal("P205", state.Series[0].Points[^1].Label);
    }

    [Fact]
    public async Task TabsNeuronHoldsItsSelection()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var tabs = os.Get<ITabs>(new NeuronId(Brain, "ui.tabs", "nav"));
        await tabs.Configure([new UiTabItem("home", "Home"), new UiTabItem("data", "Data")], "home");
        Assert.Equal("data", (await tabs.Select("data")).SelectedId);
    }

    [Fact]
    public async Task TableNeuronBindsToSampleSourceFiltersSortsAndSelectsRows()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var table = os.Get<ITable>(new NeuronId(Brain, "ui.table", "spans"));
        var sourceAddress = new NeuronId(Brain, "ui.sample-table-source", "default").ToString();
        await table.Bind(sourceAddress, "Silo Spans", [new TableColumn("span", "Span"), new TableColumn("status", "Status"), new TableColumn("latency", "Latency"), new TableColumn("module", "Module")], pageSize: 5);

        var initial = await table.Read();
        Assert.Equal(8, initial.Total);
        Assert.Equal(5, initial.Rows.Length);

        var filtered = await table.Filter("Modules.UI");
        Assert.Equal(3, filtered.Total);

        var sorted = await table.Sort("span");
        Assert.Equal("span", sorted.SortBy);
        Assert.False(sorted.Descending);

        var selected = await table.SelectRow(1);
        Assert.Equal(1, selected.SelectedRowIndex);
    }

    [Fact]
    public async Task FormNeuronRefusesPlaintextSecretsAndAcceptsSecretRef()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var form = os.Get<IForm>(new NeuronId(Brain, "ui.form", "api-keys"));
        await form.Configure("API Keys",
        [
            new FormField("provider", "Provider", FormFieldKinds.Text, Required: true),
            new FormField("apiKey", "API Key", FormFieldKinds.Secret, Required: true),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => form.SetValue("apiKey", "sk-plaintext"));
        await form.SetValue("provider", "Anthropic");
        await form.SetSecretRef("apiKey", new UiSecretRef("vault://ANTHROPIC_API_KEY"));

        var submitted = await form.Submit();
        Assert.Null(submitted.Error);
        Assert.Equal("Anthropic", submitted.Values["provider"]);
        Assert.Equal("vault://ANTHROPIC_API_KEY", submitted.SecretRefs["apiKey"].VaultKey);
    }

    [Fact]
    public async Task DialogNeuronOpensConfirmsWithInputAndCloses()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var dialog = os.Get<IDialog>(new NeuronId(Brain, "ui.dialog", "mkdir"));
        var opened = await dialog.Open("New folder", "Enter folder name", "Create", "Cancel", inputLabel: "Folder name");
        Assert.True(opened.IsOpen);

        var confirmed = await dialog.Confirm("reports");
        Assert.False(confirmed.IsOpen);
        Assert.Equal("reports", confirmed.InputValue);
    }

    [Fact]
    public async Task ChatThreadNeuronStreamsDeltasReasoningToolCallsAndContext()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var chat = os.Get<IChatThread>(new NeuronId(Brain, "ui.chat", "ino/main"));
        await chat.Configure("main", "Ino Workbench", "claude-sonnet-4-5",
            [new ChatModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5"), new ChatModelOption("gpt-5-thinking", "GPT-5 Thinking")],
            voiceAvailable: true);

        await chat.StreamDelta("m1", textDelta: "Checking ", reasoningDelta: "Plan: inspect chart", status: "streaming");
        await chat.UpsertToolCall("m1", new ChatToolCall("c1", "neurons_call", "{\"contract\":\"ui.chart\"}", "{\"ok\":true}", "done", 18));
        await chat.StreamDelta("m1", textDelta: "revenue.", status: "done");
        await chat.SetContext([new ChatContextItem("memory.recall", "Owner prefers warm-paper", "owner", 120)], "Compacted 12 turns", 1400, 320);
        await chat.SelectModel("gpt-5-thinking");

        var state = await chat.Read();
        Assert.Equal("gpt-5-thinking", state.ActiveModel);
        Assert.Single(state.Messages);
        Assert.Equal("Checking revenue.", state.Messages[0].Text);
        Assert.Equal("Plan: inspect chart", state.Messages[0].Reasoning);
        Assert.Single(state.Messages[0].ToolCalls!);
        Assert.Equal("neurons_call", state.Messages[0].ToolCalls![0].Name);
        Assert.Equal(1400, state.TotalInputTokens);
        Assert.Equal("Compacted 12 turns", state.ContextSummary);
    }

    [Fact]
    public async Task WinUi3Phase4NeuronsMaintainStateAndInvariants()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var slider = os.Get<ISlider>(new NeuronId(Brain, "ui.slider", "s1"));
        await slider.Configure("Budget", 0, 100, 5, 50, unit: "%");
        Assert.Equal(100, (await slider.SetValue(150)).Value); // clamped to max

        var crumb = os.Get<IBreadcrumbBar>(new NeuronId(Brain, "ui.breadcrumb", "b1"));
        await crumb.SetItems([new BreadcrumbItem("root", "Root"), new BreadcrumbItem("docs", "Docs"), new BreadcrumbItem("q4", "Q4")]);
        var nav = await crumb.NavigateTo("docs");
        Assert.Equal(2, nav.Items.Length);
        Assert.Equal("docs", nav.ActiveId);

        var tree = os.Get<ITreeView>(new NeuronId(Brain, "ui.tree-view", "tv1"));
        await tree.Configure([new TreeNode("code", "code", IsFolder: true), new TreeNode("a", "A.cs", ParentId: "code")]);
        Assert.Contains("code", (await tree.ToggleExpand("code")).ExpandedIds);
        Assert.Equal("a", (await tree.Select("a")).SelectedId);
    }

    [Fact]
    public async Task WinUi3Tier2And3MenuAndGridNeuronsMaintainState()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var menu = os.Get<IContextMenu>(new NeuronId(Brain, "ui.context-menu", "cm1"));
        await menu.Configure("Actions", [new ContextMenuItem("open", "Open"), new ContextMenuItem("del", "Delete", Destructive: true)]);
        var mNext = await menu.Invoke("open");
        Assert.Equal(("open", 1), (mNext.LastInvokedItemId, mNext.InvokeCount));

        var grid = os.Get<IGridView>(new NeuronId(Brain, "ui.grid-view", "gv1"));
        await grid.Configure("Files", [new GridViewItem("a", "A.md"), new GridViewItem("b", "B.png")], "a", 2);
        Assert.Equal("b", (await grid.Select("b")).SelectedId);
    }

    [Fact]
    public async Task WinUi3Wave2WorkspaceKitNeuronsMaintainState()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var cb = os.Get<ICommandBar>(new NeuronId(Brain, "ui.command-bar", "cb1"));
        await cb.Configure([new CommandBarItem("run", "Run"), new CommandBarItem("export", "Export", Primary: false)]);
        var cbNext = await cb.Invoke("run");
        Assert.Equal(("run", 1), (cbNext.LastInvokedId, cbNext.InvokeCount));

        var ed = os.Get<ICodeEditor>(new NeuronId(Brain, "ui.code-editor", "ed1"));
        await ed.Configure("Demo.cs", "csharp", "var x = 1;");
        Assert.True((await ed.SetContent("var x = 2;")).Dirty);
        Assert.False((await ed.SaveContent()).Dirty);

        var md = os.Get<IMarkdownViewer>(new NeuronId(Brain, "ui.markdown", "md1"));
        var mdNext = await md.SetDocument("Spec", "# Overview\n## Details\nBody");
        Assert.Equal(["Overview", "Details"], mdNext.Headings);
    }

    [Fact]
    public async Task WinUi3Wave3StepperPropertyGridAndKanbanBoardMaintainStateAndInvariants()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        // 1. IStepper (ui.stepper)
        var stepper = os.Get<IStepper>(new NeuronId(Brain, "ui.stepper", "pipeline"));
        var stInit = await stepper.Configure(
        [
            new StepperStep("plan", "Plan", "running"),
            new StepperStep("code", "Code", "pending"),
            new StepperStep("verify", "Verify", "pending"),
        ], title: "Engineer");
        Assert.Equal("plan", stInit.ActiveStepId);
        Assert.Equal(StepperStatuses.Active, stInit.Steps[0].Status);

        var stAdv = await stepper.Advance();
        Assert.Equal("code", stAdv.ActiveStepId);
        Assert.Equal(StepperStatuses.Done, stAdv.Steps[0].Status);
        Assert.Equal(StepperStatuses.Active, stAdv.Steps[1].Status);

        var stErr = await stepper.SetStepStatus("verify", "failed", "1 test failed");
        Assert.Equal(StepperStatuses.Error, stErr.Steps[2].Status);
        Assert.Equal("1 test failed", stErr.Steps[2].Subtitle);
        Assert.Equal("verify", (await stepper.Select("verify")).ActiveStepId);
        await Assert.ThrowsAsync<ArgumentException>(() => stepper.Select("missing"));

        var lockedStepper = os.Get<IStepper>(new NeuronId(Brain, "ui.stepper", "locked"));
        await lockedStepper.Configure([new StepperStep("s1", "One")], allowInteractiveSelection: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lockedStepper.Select("s1"));

        // 2. IPropertyGrid (ui.property-grid)
        var grid = os.Get<IPropertyGrid>(new NeuronId(Brain, "ui.property-grid", "inspector"));
        var pgReadOnly = await grid.LoadJson("Neuron State", "{\"count\":42,\"nested\":{\"active\":true},\"items\":[\"a\",\"b\"]}", readOnly: true);
        Assert.Contains(pgReadOnly.Entries, e => e.Path == "count" && e.Value == "42" && e.ValueKind == "number");
        Assert.Contains(pgReadOnly.Entries, e => e.Path == "nested.active" && e.Value == "true" && e.ValueKind == "boolean");
        Assert.Contains(pgReadOnly.Entries, e => e.Path == "items[1]" && e.Value == "b" && e.ValueKind == "string");
        await Assert.ThrowsAsync<InvalidOperationException>(() => grid.SetValue("count", "99"));

        await grid.LoadJson("Editable State", "{\"count\":42}", readOnly: false);
        var pgEdited = await grid.SetValue("count", "99");
        Assert.Equal("99", pgEdited.Entries.Single(e => e.Path == "count").Value);
        Assert.Equal("42", (await grid.SetFilter("42")).Filter);
        var toggled = await grid.ToggleExpand("nested");
        Assert.Contains("nested", toggled.ExpandedPaths);

        // 3. IKanbanBoard (ui.kanban)
        var kanban = os.Get<IKanbanBoard>(new NeuronId(Brain, "ui.kanban", "sprint"));
        await kanban.Configure(
            "Sprint",
            [new KanbanColumn("todo", "To Do"), new KanbanColumn("doing", "Doing", WipLimit: 1), new KanbanColumn("done", "Done")],
            [new KanbanCard("k1", "todo", "Task 1"), new KanbanCard("k2", "todo", "Task 2")]);

        var moved1 = await kanban.MoveCard("k1", "doing");
        Assert.Equal("doing", moved1.Cards.Single(c => c.Id == "k1").ColumnId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kanban.MoveCard("k2", "doing")); // WIP limit = 1

        var upserted = await kanban.UpsertCard(new KanbanCard("k3", "done", "Shipped"));
        Assert.Equal(3, upserted.Cards.Length);
        var removed = await kanban.RemoveCard("k2");
        Assert.Equal(2, removed.Cards.Length);
    }

    [Fact]
    public void GalleryIconCatalogCoversAllEightWinUiCategories()
    {
        string[] expected = ["OS", "Code/DDD", "AI/Neurons", "Files", "Media", "Navigation", "Status", "Devices"];
        foreach (var category in expected)
        {
            Assert.NotEmpty(IntoChat.OS.Apps.Gallery.GalleryIconCatalog.ForCategory(category));
        }
        Assert.True(IntoChat.OS.Apps.Gallery.GalleryIconCatalog.All.Length >= 100);
    }

    [Fact]
    public void WinUi3GalleryRendersEveryPageWithAppShellRoot()
    {
        foreach (var page in IntoChat.OS.Apps.Gallery.GalleryAppState.PageIds)
        {
            var state = new IntoChat.OS.Apps.Gallery.GalleryAppState().SelectPage(page);
            var view = IntoChat.OS.Apps.Gallery.GalleryRendering.Render(state);
            var shell = Assert.IsType<DigitalBrain.UI.AppShellNode>(view.Root);
            Assert.NotNull(shell.Sidebar);
            Assert.NotNull(shell.Toolbar);
            Assert.NotNull(shell.Body);
        }
    }
}
