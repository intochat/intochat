using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.UI.AutoSuggestBox;
using IntoChat.OS.Modules.UI.BreadcrumbBar;
using IntoChat.OS.Modules.UI.CalendarView;
using IntoChat.OS.Modules.UI.Chart;
using IntoChat.OS.Modules.UI.Chart.Signals;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CheckBox;
using IntoChat.OS.Modules.UI.ColorPicker;
using IntoChat.OS.Modules.UI.ContextMenu;
using IntoChat.OS.Modules.UI.DatePicker;
using IntoChat.OS.Modules.UI.Dialog;
using IntoChat.OS.Modules.UI.Expander;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.GridView;
using IntoChat.OS.Modules.UI.ImageViewer;
using IntoChat.OS.Modules.UI.InfoBar;
using IntoChat.OS.Modules.UI.ListBox;
using IntoChat.OS.Modules.UI.MediaPlayer;
using IntoChat.OS.Modules.UI.NumberBox;
using IntoChat.OS.Modules.UI.Progress;
using IntoChat.OS.Modules.UI.RadioButtons;
using IntoChat.OS.Modules.UI.Rating;
using IntoChat.OS.Modules.UI.Select;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.TimePicker;
using IntoChat.OS.Modules.UI.Toggle;
using IntoChat.OS.Modules.UI.TreeView;

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
        Assert.Equal(2, s1.Series[0].Points.Count);
        Assert.Equal("T2", s1.Series[0].Points[1].Label);

        Assert.Equal(ChartKinds.Area, s2.Kind);
        Assert.Equal(2, s2.Series.Count);
        Assert.Equal(3, s2.Series[0].Points.Count); // Jan, Feb, Mar for Q1
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
        Assert.Equal(ChartState.MaxPointsPerSeries, state.Series[0].Points.Count);
        Assert.Equal("P6", state.Series[0].Points[0].Label);
        Assert.Equal("P205", state.Series[0].Points[^1].Label);
    }

    [Fact]
    public async Task AtomicControlNeuronsHoldIndependentStateAndEnforceInvariants()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var wifi = os.Get<IToggle>(new NeuronId(Brain, "ui.toggle", "wifi"));
        var locked = os.Get<IToggle>(new NeuronId(Brain, "ui.toggle", "locked"));
        await wifi.Configure("Wi-Fi", isOn: false);
        await locked.Configure("Locked", isOn: true, isEnabled: false);

        var toggled = await wifi.Toggle();
        Assert.True(toggled.IsOn);
        await Assert.ThrowsAsync<InvalidOperationException>(locked.Toggle);

        var check = os.Get<ICheckBox>(new NeuronId(Brain, "ui.checkbox", "remember"));
        await check.Configure("Remember me", isChecked: false);
        Assert.True((await check.Toggle()).IsChecked);

        var select = os.Get<ISelect>(new NeuronId(Brain, "ui.select", "model"));
        await select.Configure("Model", [new SelectOption("a", "Model A"), new SelectOption("b", "Model B")], "a");
        Assert.Equal("b", (await select.Select("b")).SelectedId);

        var tabs = os.Get<ITabs>(new NeuronId(Brain, "ui.tabs", "nav"));
        await tabs.Configure([new TabItem("home", "Home"), new TabItem("data", "Data")], "home");
        Assert.Equal("data", (await tabs.Select("data")).SelectedId);

        var expander = os.Get<IExpander>(new NeuronId(Brain, "ui.expander", "details"));
        await expander.Configure("Details", "Hidden body", isExpanded: false);
        Assert.True((await expander.Toggle()).IsExpanded);

        var listBox = os.Get<IListBox>(new NeuronId(Brain, "ui.listbox", "people"));
        await listBox.Configure([new ListBoxItem("ana", "Ana"), new ListBoxItem("ben", "Ben")], "ana");
        Assert.Equal("ben", (await listBox.Select("ben")).SelectedId);
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
        Assert.Equal(5, initial.Rows.Count);

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
        await form.SetSecretRef("apiKey", new SecretRef("vault://ANTHROPIC_API_KEY"));

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

        var radio = os.Get<IRadioButtons>(new NeuronId(Brain, "ui.radio-buttons", "r1"));
        await radio.Configure("Mode", [new RadioOption("a", "A"), new RadioOption("b", "B")], "a");
        Assert.Equal("b", (await radio.Select("b")).SelectedId);

        var num = os.Get<INumberBox>(new NeuronId(Brain, "ui.number-box", "n1"));
        await num.Configure("Cap", 50, 10, 100, 10, prefix: "$");
        Assert.Equal(60, (await num.Increment()).Value);
        Assert.Equal(50, (await num.Decrement()).Value);

        var rating = os.Get<IRating>(new NeuronId(Brain, "ui.rating", "rt1"));
        await rating.Configure("Quality", 3, 5);
        Assert.Equal(5, (await rating.SetValue(9)).Value); // clamped to max

        var color = os.Get<IColorPicker>(new NeuronId(Brain, "ui.color-picker", "c1"));
        await color.Configure("Accent", "#1F5EFF");
        Assert.Equal("#10B981", (await color.SetColor("10b981")).Hex);

        var crumb = os.Get<IBreadcrumbBar>(new NeuronId(Brain, "ui.breadcrumb", "b1"));
        await crumb.SetItems([new BreadcrumbItem("root", "Root"), new BreadcrumbItem("docs", "Docs"), new BreadcrumbItem("q4", "Q4")]);
        var nav = await crumb.NavigateTo("docs");
        Assert.Equal(2, nav.Items.Count);
        Assert.Equal("docs", nav.ActiveId);

        var suggest = os.Get<IAutoSuggestBox>(new NeuronId(Brain, "ui.auto-suggest", "as1"));
        await suggest.Configure("Search", ["IChart", "ITable", "IToggle"]);
        Assert.Single((await suggest.SetQuery("chart")).Suggestions);

        var tree = os.Get<ITreeView>(new NeuronId(Brain, "ui.tree-view", "tv1"));
        await tree.Configure([new TreeNode("src", "src", IsFolder: true), new TreeNode("a", "A.cs", ParentId: "src")]);
        Assert.Contains("src", (await tree.ToggleExpand("src")).ExpandedIds);
        Assert.Equal("a", (await tree.Select("a")).SelectedId);

        var prog = os.Get<IProgressIndicator>(new NeuronId(Brain, "ui.progress", "p1"));
        Assert.Equal(0.75, (await prog.SetProgress("Indexing", 0.75)).Value);

        var info = os.Get<IInfoBar>(new NeuronId(Brain, "ui.info-bar", "i1"));
        Assert.True((await info.Show("Saved", "Done", "success")).IsOpen);
        Assert.False((await info.Dismiss()).IsOpen);
    }

    [Fact]
    public async Task WinUi3Tier2And3DateTimeMenuGridAndMediaNeuronsMaintainState()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(UiModule).Assembly);

        var dp = os.Get<IDatePicker>(new NeuronId(Brain, "ui.date-picker", "dp1"));
        await dp.Configure("Launch", "2026-11-19", "2026-01-01", "2026-12-31");
        Assert.Equal("2026-12-31", (await dp.SetDate("2028-05-01")).SelectedDate); // clamped to MaxDate

        var tp = os.Get<ITimePicker>(new NeuronId(Brain, "ui.time-picker", "tp1"));
        await tp.Configure("Standup", 9, 30);
        var tNext = await tp.SetTime(14, 45);
        Assert.Equal((14, 45), (tNext.Hour, tNext.Minute));

        var cal = os.Get<ICalendarView>(new NeuronId(Brain, "ui.calendar", "cal1"));
        await cal.Configure("Sprint", 2026, 11, ["2026-11-19"], multiSelect: true);
        var cNext = await cal.ToggleDate("2026-11-24");
        Assert.Equal(2, cNext.SelectedDates.Count);

        var menu = os.Get<IContextMenu>(new NeuronId(Brain, "ui.context-menu", "cm1"));
        await menu.Configure("Actions", [new ContextMenuItem("open", "Open"), new ContextMenuItem("del", "Delete", Destructive: true)]);
        var mNext = await menu.Invoke("open");
        Assert.Equal(("open", 1), (mNext.LastInvokedItemId, mNext.InvokeCount));

        var grid = os.Get<IGridView>(new NeuronId(Brain, "ui.grid-view", "gv1"));
        await grid.Configure("Files", [new GridViewItem("a", "A.md"), new GridViewItem("b", "B.png")], "a", 2);
        Assert.Equal("b", (await grid.Select("b")).SelectedId);

        var img = os.Get<IImageViewer>(new NeuronId(Brain, "ui.image-viewer", "iv1"));
        await img.Configure("Preview", "https://example.com/a.png");
        Assert.Equal(1.5, (await img.SetZoom(1.5)).Zoom);
        Assert.Equal(90, (await img.Rotate(90)).RotationDegrees);
        Assert.Equal((1.0, 0), ((await img.ResetView()).Zoom, (await img.Read()).RotationDegrees));

        var mp = os.Get<IMediaPlayer>(new NeuronId(Brain, "ui.media-player", "mp1"));
        await mp.Configure("Track", "Artist", "", "audio", 200);
        Assert.True((await mp.Play()).Playing);
        Assert.Equal(42, (await mp.Seek(42)).PositionSeconds);
        Assert.False((await mp.Pause()).Playing);
        Assert.True((await mp.SetVolume(0.5, muted: true)).Muted);
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
