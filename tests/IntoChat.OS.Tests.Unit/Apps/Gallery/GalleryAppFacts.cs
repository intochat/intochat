using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Gallery;

// The UI Gallery (that every catalog kind has a page is GalleryCatalogFacts, beside the catalog generator): a page per catalog kind and per chat scenario, real control neurons slotted on them, and a log of every signal they send.
public sealed class GalleryAppFacts
{
    private static readonly BrainId Brain = new("local");

    private static async Task<InstalledApp<GalleryApp>> Gallery(TestOS os, string page)
    {
        RegisterControls(os);
        var gallery = await os.Install<GalleryApp>();
        await gallery.Request("open", new GalleryOpen(page));
        return gallery;
    }

    private static async Task Send(InstalledApp<GalleryApp> gallery, string slot, Signal command)
    {
        var alias = command.GetType().GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias;
        using (gallery.OS.Edge.Enter(gallery.OS.Edge.Person("local", "local", Brain)))
        {
            await gallery.Screen.Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));
        }
        await gallery.OS.Idle();
    }

    private static async Task<T> Drawn<T>(TestOS os, string type, string slot) where T : Node
    {
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var id = new NeuronId(Brain, type, ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.gallery", "default"), slot));
        var view = await os.Get<IScreen>(id).Read() ?? throw new InvalidOperationException($"{id} has no view.");
        return Assert.IsType<T>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task Every_page_renders_without_error()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.First);
        foreach (var page in GalleryPages.All)
        {
            var view = gallery.App.PreviewView(gallery.App.State with { Page = page.Id });
            Assert.Equal("UI Gallery", view.Title);
            JsonSerializer.SerializeToElement(view, JsonSerializerOptions.Web);
        }
    }

    [Fact]
    public async Task Child_signal_lands_in_log()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, "checkBox");

        await Send(gallery, "checkBox", new SetChecked(true));

        var entry = gallery.App.State.Log[^1];
        Assert.Equal(("checkBox", "control.toggled"), (entry.Control, entry.Signal));
        Assert.Equal("""{"isOn":true}""", entry.ArgsJson);
    }

    [Fact]
    public async Task Log_capped_at_50()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, "checkBox");

        for (var i = 0; i < GalleryState.MaxLog + 5; i++) { await Send(gallery, "checkBox", new SetChecked(i % 2 == 0)); }

        Assert.Equal(GalleryState.MaxLog, gallery.App.State.Log.Length);
        Assert.Equal("""{"isOn":true}""", gallery.App.State.Log[^1].ArgsJson); // 55 values, alternating from on, end on
    }

    [Fact]
    public async Task Chart_page_append_updates_view()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.LiveChart);

        await gallery.Request("append", new GalleryAppend());
        await gallery.Request("append", new GalleryAppend());

        var chart = await Drawn<LiveChartView>(os, "control.liveChart", GalleryPages.LiveChart);
        Assert.Equal(["Mon", "Tue", "Wed", "#1", "#2"], chart.Points.Select(point => point.Label));
        Assert.Contains(gallery.App.State.Log, entry => entry is { Control: GalleryPages.LiveChart, Signal: "control.chartChanged" });
    }

    [Fact]
    public async Task Failed_reply_page_writes_its_failed_message_on_show()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        _ = await Gallery(os, GalleryPages.FailedReply);

        var thread = await Drawn<ChatThreadView>(os, "control.chatThread", GalleryPages.FailedReply);
        Assert.Equal(["user", "assistant"], thread.Messages.Select(message => message.Role));
        Assert.Equal("failed", thread.Messages[^1].Status);
    }

    [Fact]
    public async Task The_controlled_switch_stores_its_value_in_the_app()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, "toggleSwitch");

        await Send(gallery, GalleryPages.ControlledSwitch, new SetChecked(true));

        Assert.True(gallery.App.State.ControlledOn);
    }

    [Fact]
    public async Task A_kanban_move_lands_in_the_app_cards_and_a_refused_one_does_not()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, "kanban");

        await Send(gallery, "kanban", new MoveCard("notes", "done", 0));
        await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(gallery, "kanban", new MoveCard("plan", "nowhere", 0)));

        Assert.Equal(["ship", "notes", "plan"], gallery.App.State.Cards.Select(card => card.Id));
        Assert.Equal("done", gallery.App.State.Cards.Single(card => card.Id == "notes").ColumnId);
        Assert.Contains(gallery.App.State.Log, entry => entry.Signal == "control.cardMoved");
    }

    [Fact]
    public async Task The_nav_switches_pages()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.First);

        await Send(gallery, GalleryPages.Nav, new DigitalBrain.UI.Commands.Select("form"));

        Assert.Equal("form", gallery.App.State.Page);
    }

    [Fact]
    public async Task The_chart_page_is_seeded_once()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.LiveChart);
        await gallery.Request("open", new GalleryOpen(GalleryPages.First));
        await gallery.Request("open", new GalleryOpen(GalleryPages.LiveChart));

        var chart = await Drawn<LiveChartView>(os, "control.liveChart", GalleryPages.LiveChart);
        Assert.Equal(["Mon", "Tue", "Wed"], chart.Points.Select(point => point.Label));
    }

    [Fact]
    public async Task Every_data_node_example_command_lands_in_the_log()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.First);
        var clicked = 0;
        foreach (var page in GalleryPages.All)
        {
            await gallery.Request("open", new GalleryOpen(page.Id));
            using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
            var view = await gallery.Screen.Read() ?? throw new InvalidOperationException("no view");
            foreach (var command in Commands(view.View))
            {
                var signal = command.GetProperty("signal").GetString();
                if (page.Group != GalleryPages.DataNode) { continue; }
                Assert.Equal(GalleryPages.ShowcaseCommandSignal, signal);
                var before = gallery.App.State.Log.Length;
                await gallery.Screen.Dispatch(new Command(".", signal!, command.GetProperty("args")));
                await os.Idle();
                var entry = gallery.App.State.Log[^1];
                Assert.Equal(page.Id, entry.Control);
                Assert.Equal(Math.Min(before + 1, GalleryState.MaxLog), gallery.App.State.Log.Length);
                clicked++;
            }
        }
        Assert.True(clicked >= 4, $"Only {clicked} example commands were found.");
        Assert.Contains(gallery.App.State.Log, entry => entry.Signal == "Saved");
    }

    [Fact]
    public async Task The_nav_groups_its_pages()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.First);

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await gallery.Screen.Read() ?? throw new InvalidOperationException("no view");
        var nav = Slots(view.View).Single(slot => slot.GetProperty("neuronUri").GetString()!.EndsWith("/nav", StringComparison.Ordinal));
        var groups = nav.GetProperty("props").GetProperty("items").EnumerateArray().Select(item => item.GetProperty("group").GetString()).Distinct();
        Assert.Equal(new[] { GalleryPages.Scenario, GalleryPages.ControlNeuron, GalleryPages.DataNode }.Order(StringComparer.Ordinal), groups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_gallery_keeps_its_id_title_icon_and_dock_place()
    {
        var neuron = typeof(GalleryApp).GetCustomAttributes(typeof(DigitalBrain.Neurons.NeuronAttribute), false).Cast<DigitalBrain.Neurons.NeuronAttribute>().Single();
        var screen = typeof(GalleryApp).GetCustomAttributes(typeof(IntoChat.OS.UI.ScreenAttribute), false).Cast<IntoChat.OS.UI.ScreenAttribute>().Single();
        Assert.Equal("app.gallery", neuron.Type);
        Assert.Equal(("app.gallery", "UI Gallery", "layout-grid", "Studio", -1), (screen.Alias, screen.Title, screen.Icon, screen.Category, screen.Dock));
        Assert.Equal("gallery", GalleryApp.Bundle.Name);
        Assert.Null(typeof(GalleryApp).Assembly.GetType("IntoChat.OS.Apps.UiKit.UiKitApp"));
    }

    // A Gallery written by the old app (GalleryAppState: a page id of its own, an identity preset, a bank account demo and more) loads: the
    // members this Gallery does not know are ignored, a page id it also has opens that page, and any other id draws the first page.
    [Theory]
    [InlineData("form", "form")]
    [InlineData("charts", GalleryPages.First)]
    public async Task A_state_the_old_gallery_stored_loads_and_draws(string storedPage, string drawnPage)
    {
        await using var os = TestOS.Local().RegisterCatalog();
        os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly).Register<SampleTableSource>().Register<GalleryApp>();
        var id = new NeuronId(Brain, "app.gallery", "default");
        os.SeedStoredState(id, $$"""
            {"page":"{{storedPage}}","stylePreset":"cyber-synaptic","barLayout":"split-top-dock","status":"UI Gallery · Charts (IChart)",
             "account":{"isOpen":true,"holder":"Alice","balance":20,"nonNegativeBalance":true},"selectedId":"table-card","selectedGraphStateId":"s1",
             "showRejectedSelfLoops":true,"streamPointCounter":6,"revenueQueryFilter":"q1","buttonClicks":3,"iconCategory":"All",
             "codeLanguage":"csharp","lastAction":"Navigated to Charts","hasValidPage":true,"embeddedAccountHealthy":true}
            """);

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await os.Get<IScreen>(id).Read() ?? throw new InvalidOperationException("The Gallery has no view.");

        var nav = Slots(view.View).Single(slot => slot.GetProperty("neuronUri").GetString()!.EndsWith("/nav", StringComparison.Ordinal));
        Assert.Equal(drawnPage, nav.GetProperty("props").GetProperty("selectedId").GetString());
        Assert.DoesNotContain("\"ui.", view.View.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inline_table_selects_a_row_and_refuses_an_unknown_one()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, "queryTable");

        await Send(gallery, GalleryPages.InlineTable, new SelectRow("ship"));
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(gallery, GalleryPages.InlineTable, new SelectRow("nowhere")));

        Assert.Equal(TableRefusals.UnknownRow, refused.Reason);
        var entry = gallery.App.State.Log[^1];
        Assert.Equal((GalleryPages.InlineTable, "control.rowSelected"), (entry.Control, entry.Signal));
        Assert.Equal("""{"rowId":"ship"}""", entry.ArgsJson);
        var table = await Drawn<QueryTableView>(os, "control.queryTable", GalleryPages.InlineTable);
        Assert.Equal("ship", table.SelectedRowId);
    }

    [Fact]
    public async Task The_secret_field_takes_a_vault_reference_and_refuses_a_value()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, "form");
        var reference = "secret://local/gallery-api-key";

        await Send(gallery, "form", new Submit(new Dictionary<string, string> { ["name"] = "Ada" }, new Dictionary<string, string> { [GalleryPages.SecretField] = reference }));
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            Send(gallery, "form", new Submit(new Dictionary<string, string> { [GalleryPages.SecretField] = "sk-plaintext" })));

        Assert.Equal(FormRefusals.SecretInValues, refused.Reason);
        var entry = gallery.App.State.Log[^1];
        Assert.Equal(("form", "control.submitted"), (entry.Control, entry.Signal));
        Assert.Contains(reference, entry.ArgsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(gallery.App.State.Log, logged => logged.ArgsJson.Contains("sk-plaintext", StringComparison.Ordinal));
    }

    // Every button the Gallery draws sends a typed command the Gallery handles ([On]) or a child's own command; no page names a
    // method-bound ui.command or a ui.* neuron.
    [Fact]
    public async Task Every_command_on_every_page_is_one_the_gallery_or_a_child_handles()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await Gallery(os, GalleryPages.First);
        var checkedCommands = 0;
        foreach (var page in GalleryPages.All)
        {
            await gallery.Request("open", new GalleryOpen(page.Id));
            using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
            var view = await gallery.Screen.Read() ?? throw new InvalidOperationException("no view");
            Assert.DoesNotContain("ui.command", view.View.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("\"ui.", view.View.GetRawText(), StringComparison.Ordinal);
            foreach (var command in Commands(view.View).Where(command => command.GetProperty("target").GetString() == "."))
            {
                var signal = command.GetProperty("signal").GetString()!;
                Assert.True(DigitalBrain.Kernel.ScreenSignals.Find(typeof(GalleryApp), signal) is not null, $"Page '{page.Id}' sends '{signal}', which the Gallery does not handle.");
                checkedCommands++;
            }
        }
        Assert.True(checkedCommands > 0);
    }

    private static void RegisterControls(TestOS os)
        => os.Register<CheckBoxNeuron>().Register<ExpanderNeuron>().Register<TabStripNeuron>().Register<ListBoxNeuron>()
            .Register<LiveChartNeuron>().Register<QueryTableNeuron>().Register<SampleTableSource>()
            .Register<KanbanNeuron>().Register<StepperNeuron>().Register<TreeNeuron>().Register<SliderNeuron>();

    private static IEnumerable<JsonElement> Commands(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => (json.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object ? [command] : Enumerable.Empty<JsonElement>())
            .Concat(json.TryGetProperty("close", out var close) && close.ValueKind == JsonValueKind.Object ? [close] : [])
            .Concat(json.EnumerateObject().SelectMany(property => Commands(property.Value))),
        JsonValueKind.Array => json.EnumerateArray().SelectMany(Commands),
        _ => [],
    };

    private static IEnumerable<JsonElement> Slots(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => (json.TryGetProperty("refKind", out var kind) && kind.GetString() == "child" ? [json] : Enumerable.Empty<JsonElement>())
            .Concat(json.EnumerateObject().SelectMany(property => Slots(property.Value))),
        JsonValueKind.Array => json.EnumerateArray().SelectMany(Slots),
        _ => [],
    };
}
