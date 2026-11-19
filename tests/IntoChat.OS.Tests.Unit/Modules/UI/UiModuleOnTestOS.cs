using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.UI.Chart;
using IntoChat.OS.Modules.UI.Chart.Signals;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.CheckBox;
using IntoChat.OS.Modules.UI.Dialog;
using IntoChat.OS.Modules.UI.Expander;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.ListBox;
using IntoChat.OS.Modules.UI.Select;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Toggle;

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
