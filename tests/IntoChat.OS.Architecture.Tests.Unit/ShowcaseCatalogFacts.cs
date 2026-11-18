using System.Text.Json;
using DigitalBrain.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The showcase catalog lists every [ViewNode] kind with its props, events and [Showcase] examples, sorted by kind.
public sealed class ShowcaseCatalogFacts
{
    private static readonly string Target = Path.Combine(Repository.Root, "ui", "lib", "s" + "rc", "generated", "showcase_catalog.json");

    [Fact]
    public void TheShowcaseCatalogIsGenerated()
        => NodeSchema.CheckOrRegenerate(Target, NodeSchema.GenerateCatalog(), nameof(TheShowcaseCatalogIsGenerated));

    [Fact]
    public void EveryShowcaseExampleDeserialisesToItsKind()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var (kind, type) in ViewNodeAttribute.DiscoverAll())
        {
            foreach (var example in NodeSchema.Examples(kind, type))
            {
                var node = example.Deserialize<Node>(options);
                Assert.True(node?.GetType() == type, $"A showcase example of '{kind}' reads back as {node?.GetType().Name ?? "null"}: {example}");
            }
        }
    }

    [Fact]
    public void TheCatalogListsEveryViewNodeKind()
    {
        using var catalog = JsonDocument.Parse(NodeSchema.GenerateCatalog());
        var kinds = catalog.RootElement.EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal(ViewNodeAttribute.DiscoverAll().Keys.Order(StringComparer.Ordinal), kinds);
    }
}
