using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.UiKit;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.UiKit;

// The UI Kit (that every catalog kind has a page is UiKitCatalogFacts, beside the catalog generator): a page per catalog kind and per chat scenario, real control neurons slotted on them, and a log of every signal they send.
public sealed class UiKitAppFacts
{
    private static readonly BrainId Brain = new("local");

    private static async Task<InstalledApp<UiKitApp>> UiKit(TestOS os, string page)
    {
        os.Register<CheckBoxNeuron>().Register<ExpanderNeuron>().Register<TabStripNeuron>().Register<ListBoxNeuron>()
            .Register<LiveChartNeuron>().Register<QueryTableNeuron>().Register<SampleTableSource>();
        var uikit = await os.Install<UiKitApp>();
        await uikit.Request("open", new UiKitOpen(page));
        return uikit;
    }

    private static async Task Send(InstalledApp<UiKitApp> uikit, string slot, Signal command)
    {
        var alias = command.GetType().GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias;
        using (uikit.OS.Edge.Enter(uikit.OS.Edge.Person("local", "local", Brain)))
        {
            await uikit.Screen.Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));
        }
        await uikit.OS.Idle();
    }

    private static async Task<T> Drawn<T>(TestOS os, string type, string slot) where T : Node
    {
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var id = new NeuronId(Brain, type, ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.uikit", "default"), slot));
        var view = await os.Get<IScreen>(id).Read() ?? throw new InvalidOperationException($"{id} has no view.");
        return Assert.IsType<T>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task Every_page_renders_without_error()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var uikit = await UiKit(os, UiKitPages.First);
        foreach (var page in UiKitPages.All)
        {
            var view = uikit.App.PreviewView(uikit.App.State with { Page = page.Id });
            Assert.Equal("UI Kit", view.Title);
            JsonSerializer.SerializeToElement(view, JsonSerializerOptions.Web);
        }
    }

    [Fact]
    public async Task Child_signal_lands_in_log()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var uikit = await UiKit(os, "checkBox");

        await Send(uikit, "checkBox", new Toggle());

        var entry = uikit.App.State.Log[^1];
        Assert.Equal(("checkBox", "control.toggled"), (entry.Control, entry.Signal));
        Assert.Equal("""{"isOn":true}""", entry.ArgsJson);
    }

    [Fact]
    public async Task Log_capped_at_50()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var uikit = await UiKit(os, "checkBox");

        for (var i = 0; i < UiKitState.MaxLog + 5; i++) { await Send(uikit, "checkBox", new Toggle()); }

        Assert.Equal(UiKitState.MaxLog, uikit.App.State.Log.Length);
        Assert.Equal("""{"isOn":true}""", uikit.App.State.Log[^1].ArgsJson); // 55 toggles end on
    }

    [Fact]
    public async Task Chart_page_append_updates_view()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var uikit = await UiKit(os, UiKitPages.LiveChart);

        await uikit.Request("append", new UiKitAppend());
        await uikit.Request("append", new UiKitAppend());

        var chart = await Drawn<LiveChartView>(os, "control.liveChart", UiKitPages.LiveChart);
        Assert.Equal(["#1", "#2"], chart.Points.Select(point => point.Label));
        Assert.Contains(uikit.App.State.Log, entry => entry is { Control: UiKitPages.LiveChart, Signal: "control.chartChanged" });
    }

    [Fact]
    public async Task Failed_reply_page_writes_its_failed_message_on_show()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        _ = await UiKit(os, UiKitPages.FailedReply);

        var thread = await Drawn<ChatThreadView>(os, "control.chatThread", UiKitPages.FailedReply);
        Assert.Equal(["user", "assistant"], thread.Messages.Select(message => message.Role));
        Assert.Equal("failed", thread.Messages[^1].Status);
    }

    [Fact]
    public async Task The_controlled_switch_stores_its_value_in_the_app()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var uikit = await UiKit(os, "toggleSwitch");

        await Send(uikit, UiKitPages.ControlledSwitch, new Toggle());

        Assert.True(uikit.App.State.ControlledOn);
    }

    [Fact]
    public async Task The_nav_switches_pages()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var uikit = await UiKit(os, UiKitPages.First);

        await Send(uikit, UiKitPages.Nav, new DigitalBrain.UI.Commands.Select("form"));

        Assert.Equal("form", uikit.App.State.Page);
    }
}
