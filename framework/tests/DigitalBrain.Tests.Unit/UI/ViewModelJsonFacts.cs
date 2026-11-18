using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Invocation;
using DigitalBrain.Units;
using DigitalBrain.UI;

namespace DigitalBrain.Tests.Unit.UI;

public sealed class ViewModelJsonFacts
{
    private static readonly string[] ActionMembers = ["Command", "Emit", "Intent", "Resolve", "OnSubmit"];

    private static readonly Type[] ActionCarriers = [typeof(Button), typeof(IconButton), typeof(TabItem), typeof(Switch), typeof(SelectOption), typeof(Field), typeof(ApprovalCardNode), typeof(NavItemNode), typeof(ComposerNode)];

    private static bool CarriesAction(Type type) => type.GetProperties().Any(p => ActionMembers.Contains(p.Name)
        || p.PropertyType == typeof(Emit) || Nullable.GetUnderlyingType(p.PropertyType) == typeof(Emit)
        || p.PropertyType == typeof(Intent) || p.PropertyType == typeof(Resolve));

    [Fact]
    public void EveryInteractiveNodeHasAKey()
    {
        foreach (var type in ActionCarriers)
        {
            Assert.True(CarriesAction(type), $"{type.Name} carries no action.");
            Assert.True(type.GetProperty("Key")?.PropertyType == typeof(string), $"{type.Name} has no string Key.");
        }
    }

    [Fact]
    public void OnlyInteractiveTypesDeclareAnAction()
    {
        var model = typeof(Node).Assembly.GetTypes().Where(t => t.Namespace == typeof(Node).Namespace && t.IsPublic && t != typeof(Emit) && t != typeof(Command) && t != typeof(Resolve) && t != typeof(Intent));

        Assert.Equal(ActionCarriers.Select(t => t.Name).Order(StringComparer.Ordinal), model.Where(CarriesAction).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoEmitTargetsTheConfirmationsResolve()
    {
        string? root = null;
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "src", "DigitalBrain.UI"))) { root = dir; break; }
            var framework = Path.Combine(dir, "framework");
            if (Directory.Exists(Path.Combine(framework, "src", "DigitalBrain.UI"))) { root = framework; break; }
        }
        Assert.NotNull(root);
        var needle = "new Emit(\"" + "confirmations\"";
        var sep = Path.DirectorySeparatorChar;
        var offenders = new[] { "src" + sep + "DigitalBrain.UI", "src" + sep + "DigitalBrain.Testing", "tests" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root!, dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep))
            .Where(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void ButtonRoundTripsWithCommand()
    {
        var view = new View("v", new Column([new Button("go", "Go", new Command(".", "files.open", JsonSerializer.SerializeToElement(new { id = 1 })))]));
        var options = RuntimeProtocol.OptionsFor(typeof(View));

        var element = JsonSerializer.SerializeToElement(view, options);
        var button = element.GetProperty("root").GetProperty("children")[0];

        Assert.Equal("button", button.GetProperty("kind").GetString());
        Assert.Equal("go", button.GetProperty("key").GetString());
        var command = button.GetProperty("command");
        Assert.Equal((".", "files.open", 1), (command.GetProperty("target").GetString(), command.GetProperty("signal").GetString(), command.GetProperty("args").GetProperty("id").GetInt32()));
        Assert.False(button.TryGetProperty("emit", out _));
        Assert.Equal("go", button.GetProperty("automationName").GetString());
        var found = ViewNodes.Find(element, "go");
        Assert.NotNull(found);
        var sent = ViewNodes.CommandOf(found.Value)!;
        Assert.Equal((".", "files.open", 1), (sent.Target, sent.Signal, sent.Args!.Value.GetProperty("id").GetInt32()));
        var again = Assert.IsType<Button>(Assert.IsType<Column>(JsonSerializer.Deserialize<View>(element.GetRawText(), options)!.Root).Children[0]);
        Assert.Equal("files.open", again.Command!.Signal);
        Assert.Null(again.Intent);
    }

    [Fact]
    public void An_icon_button_carries_one_action_and_names_itself_by_key()
    {
        Assert.Throws<ArgumentException>(() => new IconButton("r", "refresh-cw", "Refresh"));
        var icon = new IconButton("r", "refresh-cw", "Refresh", new Command(".", "refreshed"));

        var wire = JsonSerializer.SerializeToElement<Node>(icon, RuntimeProtocol.OptionsFor(typeof(View)));

        Assert.Equal("iconButton", wire.GetProperty("kind").GetString());
        Assert.Equal("r", wire.GetProperty("automationName").GetString());
        Assert.Equal("Refresh", wire.GetProperty("tooltip").GetString());
        Assert.Equal(icon, JsonSerializer.Deserialize<Node>(wire.GetRawText(), RuntimeProtocol.OptionsFor(typeof(View))));
    }

    [Fact]
    public void A_stored_button_emit_reads_as_a_command_to_its_own_view()
    {
        const string stored = """{"title":"v","root":{"kind":"button","key":"go","label":"Go","emit":{"owner":"app.files","signal":"files.open","args":{"id":1}}}}""";
        var options = RuntimeProtocol.OptionsFor(typeof(View));

        var button = Assert.IsType<Button>(JsonSerializer.Deserialize<View>(stored, options)!.Root);

        Assert.Equal((".", "files.open", 1), (button.Command!.Target, button.Command.Signal, button.Command.Args!.Value.GetProperty("id").GetInt32()));
        var written = JsonSerializer.SerializeToElement<Node>(button, options);
        Assert.True(written.TryGetProperty("command", out _));
        Assert.False(written.TryGetProperty("emit", out _));
    }

    [Fact]
    public void ButtonCarriesExactlyOneOfEmitOrIntent()
    {
        Assert.Throws<ArgumentException>(() => new Button("a", "A"));
        Assert.Throws<ArgumentException>(() => new Button("a", "A", new Command(".", "s"), new Intent(IntentKind.Copy, "x")));
        var copy = new Button("c", "Copy", Intent: new Intent(IntentKind.Copy, "x"));

        var json = JsonSerializer.Serialize<Node>(copy);

        Assert.Contains("\"intentKind\":\"Copy\"", json, StringComparison.Ordinal);
        Assert.Equal(IntentKind.Copy, Assert.IsType<Button>(JsonSerializer.Deserialize<Node>(json)).Intent!.Kind);
        Assert.Equal("intent", typeof(Node).GetCustomAttributes<JsonDerivedTypeAttribute>().Single(a => a.DerivedType == typeof(Intent)).TypeDiscriminator);
    }

    [Fact]
    public void FieldIsKeyedAndSerialisesItsKindAsAString()
    {
        var field = new Field("q", "search", FieldKind.Search, "Find", new Emit("app", "search"));

        var json = JsonSerializer.Serialize<Node>(field);

        Assert.Contains("\"kind\":\"field\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Search\"", json, StringComparison.Ordinal);
        Assert.Equal(field, JsonSerializer.Deserialize<Node>(json));
    }

    [Fact]
    public void ApprovalCardResolvesByPlanIdNotByEmit()
    {
        var card = new ApprovalCardNode("k", "p1", "t", "s", []) { Resolve = new Resolve("p1") };

        Assert.DoesNotContain(typeof(ApprovalCardNode).GetProperties(), p => p.PropertyType == typeof(Emit) || Nullable.GetUnderlyingType(p.PropertyType) == typeof(Emit));
        Assert.Equal("p1", Assert.IsType<ApprovalCardNode>(JsonSerializer.Deserialize<Node>(JsonSerializer.Serialize<Node>(card))).Resolve!.PlanId);
    }

    [Fact]
    public void NoTypeNamedButtonActionRemains()
    {
        Assert.DoesNotContain(typeof(Node).Assembly.GetTypes(), type => type.Name == "ButtonAction");
        Assert.DoesNotContain(typeof(Node).Assembly.GetTypes(), type => type.Name == "TextField");
    }
}
