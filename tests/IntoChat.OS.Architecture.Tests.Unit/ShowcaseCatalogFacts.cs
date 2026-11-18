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
        Assert.Equal(ViewNodeAttribute.DiscoverAll().Keys.Concat(NodeSchema.ControlNeurons().Select(NodeSchema.NeuronKind)).Order(StringComparer.Ordinal), kinds);
    }

    [Fact]
    public void Every_control_neuron_has_a_showcase_example_of_a_node_it_draws()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var neuron in NodeSchema.ControlNeurons())
        {
            var examples = NodeSchema.NeuronExamples(neuron).ToArray();
            Assert.True(examples.Length > 0, $"{neuron.Name} has no [Showcase] example on its state.");
            foreach (var example in examples)
            {
                var node = example.Deserialize<Node>(options);
                Assert.True(node?.GetType().Namespace == typeof(DigitalBrain.UI.Controls.ControlRefusals).Namespace,
                    $"A showcase example of {neuron.Name} is not a control view node: {example}");
            }
        }
    }
}
