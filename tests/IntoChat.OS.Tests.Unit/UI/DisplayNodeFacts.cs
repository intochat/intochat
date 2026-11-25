using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

// The two display nodes that replace ui.image-viewer and ui.property-grid: plain data on the wire. Zoom, rotation, expansion and
// the filter belong to the client, so neither node carries them and neither has a command.
public sealed class DisplayNodeFacts
{
    private static JsonElement Wire(Node node)
        => JsonDocument.Parse(JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)))).RootElement;

    [Fact]
    public void An_image_is_an_image_node_with_its_source_caption_height_and_fit()
    {
        var wire = Wire(Kit.Image("https://example.test/a.png", "Preview", height: 200, fit: ImageFit.Cover));

        Assert.Equal("image", wire.GetProperty("kind").GetString());
        Assert.Equal("https://example.test/a.png", wire.GetProperty("url").GetString());
        Assert.Equal("Preview", wire.GetProperty("caption").GetString());
        Assert.Equal(200, wire.GetProperty("height").GetDouble());
        Assert.Equal("Cover", wire.GetProperty("fit").GetString());
        Assert.False(wire.TryGetProperty("zoom", out _));
        Assert.False(wire.TryGetProperty("rotation", out _));
    }

    [Fact]
    public void An_image_fits_inside_its_box_unless_told_otherwise()
        => Assert.Equal("Contain", Wire(Kit.Image("data:image/png;base64,AA==")).GetProperty("fit").GetString());

    [Fact]
    public void A_json_tree_carries_its_title_and_the_json_text()
    {
        var wire = Wire(Kit.JsonTree("State", """{"balance":42,"owners":["a","b"]}"""));

        Assert.Equal("jsonTree", wire.GetProperty("kind").GetString());
        Assert.Equal("State", wire.GetProperty("title").GetString());
        Assert.Equal("""{"balance":42,"owners":["a","b"]}""", wire.GetProperty("json").GetString());
        Assert.False(wire.TryGetProperty("filter", out _));
        Assert.False(wire.TryGetProperty("expanded", out _));
    }

    [Fact]
    public void Both_nodes_read_back_from_the_wire()
    {
        var options = RuntimeProtocol.OptionsFor(typeof(Node));
        Node[] nodes = [Kit.Image("https://example.test/a.png", "A"), Kit.JsonTree("State", "{}")];
        foreach (var node in nodes)
        {
            Assert.Equal(node, JsonSerializer.Deserialize<Node>(JsonSerializer.Serialize(node, options), options));
        }
    }
}
