using System.Text.Json;
using IntoChat.OS.Apps.UiKit;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The UI Kit shows every kind the showcase catalog lists: a control neuron kind as a slotted child, a data node kind from its examples.
public sealed class UiKitCatalogFacts
{
    [Fact]
    public void Lists_every_catalog_kind()
    {
        using var catalog = JsonDocument.Parse(NodeSchema.GenerateCatalog());
        foreach (var entry in catalog.RootElement.EnumerateArray())
        {
            var kind = entry.GetProperty("kind").GetString()!;
            var page = UiKitPages.Find(kind);
            Assert.True(page is not null, $"The UI Kit has no page for '{kind}'.");
            Assert.Equal(entry.GetProperty("isNeuron").GetBoolean() ? UiKitPages.ControlNeuron : UiKitPages.DataNode, page.Group);
        }
    }
}
