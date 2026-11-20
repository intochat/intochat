using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoNodeCardFacts
{
    [Fact]
    public void AColumnOfTextIsAccepted()
    {
        Assert.True(InoNodeCard.TryRead("""{"kind":"column","children":[{"kind":"text","value":"Hi"}]}""", out var node, out var why));
        Assert.Null(why);
        var column = Assert.IsType<Column>(node);
        var text = Assert.IsType<Text>(Assert.Single(column.Children));
        Assert.Equal("Hi", text.Value);
    }

    [Fact]
    public void ADataTableNodeIsAccepted()
    {
        const string json = """{"kind":"dataTable","title":"Fruit","columns":["Fruit","Colour"],"rows":[["Apple","Red"],["Banana","Yellow"]]}""";
        Assert.True(InoNodeCard.TryRead(json, out var node, out var why));
        Assert.Null(why);
        var table = Assert.IsType<DataTable>(node);
        Assert.Equal("Fruit", table.Title);
        Assert.Equal(["Fruit", "Colour"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
    }

    [Theory]
    [InlineData("""{"kind":"neuronRef","refKind":"slot","neuronUri":"x"}""", "neuronRef")]
    [InlineData("""{"kind":"column","children":[{"kind":"neuronRef","refKind":"slot","neuronUri":"x"}]}""", "neuronRef")]
    [InlineData("""{"kind":"button","label":"Go","intent":{"kind":"openUrl","argument":"https://x"}}""", "action")]
    [InlineData("""{"kind":"column","children":[{"kind":"button","key":"go","label":"Go","intent":{"intentKind":"OpenWindow","arg":"https://x"}}]}""", "action")]
    [InlineData("""{"kind":"button","key":"go","label":"Go","command":{"target":".","signal":"s"}}""", "action")]
    [InlineData("""{"kind":"field","key":"f","name":"Name","fieldKind":"Text","onSubmit":{"target":".","signal":"s"}}""", "action")]
    [InlineData("""{"kind":"switch","key":"sw","label":"On","isOn":true,"emit":{"owner":"a","signal":"s"}}""", "action")]
    [InlineData("""{"kind":"nope"}""", "kind")]
    [InlineData("""{"kind":"column","children":[{"kind":"nope"}]}""", "kind")]
    [InlineData("""not json""", "JSON")]
    public void UnsafeOrBrokenTreesAreRefusedWithAReason(string json, string mentions)
    {
        Assert.False(InoNodeCard.TryRead(json, out var node, out var why));
        Assert.Null(node);
        Assert.NotNull(why);
        Assert.Contains(mentions, why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TreesOver16KiBAreRefused()
    {
        var oversized = $$"""{"kind":"text","value":"{{new string('a', 16_385)}}"}""";
        Assert.False(InoNodeCard.TryRead(oversized, out var node, out var why));
        Assert.Null(node);
        Assert.NotNull(why);
        Assert.Contains("16", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TreesDeeperThan12AreRefused()
    {
        static string NestedColumns(int depth)
        {
            var json = """{"kind":"column","children":[]}""";
            for (var i = 1; i < depth; i++)
            {
                json = $$"""{"kind":"column","children":[{{json}}]}""";
            }
            return json;
        }

        Assert.True(InoNodeCard.TryRead(NestedColumns(12), out var allowed, out _));
        Assert.NotNull(allowed);

        Assert.False(InoNodeCard.TryRead(NestedColumns(13), out var refused, out var why));
        Assert.Null(refused);
        Assert.NotNull(why);
        Assert.Contains("depth", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CardsWithoutNodeStillLoad()
    {
        var card = JsonSerializer.Deserialize(
            """{"id":"c","kind":"table","title":"t","summary":"","sourceNeuronUri":"","status":"done"}""",
            InoJson.Default.InoCard);
        Assert.NotNull(card);
        Assert.Equal("c", card!.Id);
        Assert.Equal("table", card.Kind);
        Assert.Equal("t", card.Title);
        Assert.Null(card.Node);
    }

    [Fact]
    public async Task FocusShowReturnsNodeCardOnValidTreeAndRefusesHostileTree()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local")));
        var focus = os.Get<IInoFocus>(new NeuronId(new BrainId("local"), "ino.focus", IInoFocus.DefaultKey));

        const string validJson = """{"kind":"column","children":[{"kind":"text","value":"Apple"}]}""";
        var ok = await focus.Show("Fruit", validJson);
        Assert.True(ok.Ok, ok.Text);
        Assert.Equal("Shown.", ok.Text);
        Assert.NotNull(ok.Card);
        Assert.Equal("node", ok.Card!.Kind);
        Assert.Equal("Fruit", ok.Card.Title);
        Assert.Equal(IInoFocus.DefaultKey, ok.Card.SourceNeuronUri);
        Assert.Equal("done", ok.Card.Status);
        Assert.Equal(validJson, ok.Card.Node);

        // Round-trip through InoCards.FromToolCalls (source-generated InoJson).
        var payload = JsonSerializer.Serialize(ok, JsonSerializerOptions.Web);
        var extracted = Assert.Single(InoCards.FromToolCalls([new AgentToolCall("call-1", "neurons_call", "{}", payload)]));
        Assert.Equal(ok.Card, extracted);

        // Hostile tree returns Ok=false, Card=null, actionable reason.
        var bad = await focus.Show("Bad", """{"kind":"neuronRef","refKind":"slot","neuronUri":"x"}""");
        Assert.False(bad.Ok);
        Assert.Null(bad.Card);
        Assert.Contains("neuronRef", bad.Text, StringComparison.OrdinalIgnoreCase);
    }
}
