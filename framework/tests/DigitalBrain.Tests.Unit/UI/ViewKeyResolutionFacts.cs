using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Units;
using DigitalBrain.UI;

namespace DigitalBrain.Tests.Unit.UI;

public sealed class ViewKeyResolutionFacts
{
    private static JsonElement Wire(Node root) => JsonSerializer.SerializeToElement(new View("v", root), RuntimeProtocol.OptionsFor(typeof(View)));

    private static string SignalOf(JsonElement wire, string key)
    {
        var node = ViewNodes.Find(wire, key);
        Assert.NotNull(node);
        return ViewNodes.CommandOf(node.Value)?.Signal ?? ViewNodes.EmitOf(node.Value)!.Value.Signal;
    }

    // Views stored before tabs and selects became control children still hold keyed items without a "kind"; they stay addressable.
    [Fact]
    public void FindResolvesAStoredTabItemByKey()
    {
        using var stored = JsonDocument.Parse("""{"title":"v","root":{"kind":"tabs","selectedId":"a","items":[{"key":"tab-a","id":"a","label":"A","emit":{"owner":"app","signal":"tab.a"}},{"key":"tab-b","id":"b","label":"B","emit":{"owner":"app","signal":"tab.b"}}]}}""");

        Assert.Equal("tab.b", SignalOf(stored.RootElement, "tab-b"));
    }

    [Fact]
    public void FindResolvesAStoredSelectOptionByKey()
    {
        using var stored = JsonDocument.Parse("""{"title":"v","root":{"kind":"select","label":"Sort","selected":"name","options":[{"key":"opt-name","id":"name","label":"Name","emit":{"owner":"app","signal":"sort.name"}},{"key":"opt-date","id":"date","label":"Date","emit":{"owner":"app","signal":"sort.date"}}]}}""");

        Assert.Equal("sort.date", SignalOf(stored.RootElement, "opt-date"));
    }

    [Fact]
    public void FindResolvesACardChildButton()
    {
        var choice = Wire(new ChoiceCardNode("c", "Which?", [new ChoiceOptionNode("One", Pick: new Button("pick-1", "One", new Command(".", "pick.one")))]));
        var draft = Wire(new DraftCardNode("d", "mail", "a@b", "hi", "me", Send: new Button("send", "Send", new Command(".", "draft.send")), Discard: new Button("discard", "Discard", Intent: new Intent(IntentKind.OpenSettings))));

        Assert.Equal("pick.one", SignalOf(choice, "pick-1"));
        Assert.Equal("draft.send", SignalOf(draft, "send"));
        Assert.NotNull(ViewNodes.Find(draft, "discard"));
        Assert.Null(ViewNodes.CommandOf(ViewNodes.Find(draft, "discard")!.Value));
    }

    [Fact]
    public void FindResolvesAFieldSubmit()
    {
        var wire = Wire(new Field("q", "Search", FieldKind.Search, OnSubmit: new Command(".", "search")));

        Assert.Equal("search", SignalOf(wire, "q"));
    }

    [Fact]
    public void Only_an_info_bar_reads_its_close_as_a_command()
    {
        using var stray = JsonDocument.Parse("""{"kind":"button","key":"b","label":"B","close":{"target":".","signal":"x"}}""");
        using var bar = JsonDocument.Parse("""{"kind":"infoBar","key":"i","title":"T","message":"M","close":{"target":".","signal":"dismissed"}}""");

        Assert.Null(ViewNodes.CommandOf(stray.RootElement));
        Assert.Null(ViewNodes.EmitOf(stray.RootElement));
        Assert.Equal("dismissed", ViewNodes.CommandOf(bar.RootElement)!.Signal);
    }

    [Fact]
    public void CardChildButtonsSerialiseWithTheWireShape()
    {
        var wire = Wire(new DraftCardNode("d", "mail", "a@b", "hi", "me", Send: new Button("send", "Send", new Command(".", "draft.send"))));

        var send = wire.GetProperty("root").GetProperty("send");

        Assert.Equal("send", send.GetProperty("key").GetString());
        Assert.Equal("draft.send", send.GetProperty("command").GetProperty("signal").GetString());
        // A slot declared Button (not Node) is written without the polymorphic discriminator; that is why Find keys on the action member.
        Assert.False(send.TryGetProperty("kind", out _));
    }
}
