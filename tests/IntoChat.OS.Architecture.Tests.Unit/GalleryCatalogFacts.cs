using System.Text.Json;
using IntoChat.OS.Apps.Gallery;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The UI Gallery shows every kind the showcase catalog lists: a control neuron kind as a slotted child, a data node kind from its examples.
public sealed class GalleryCatalogFacts
{
    [Fact]
    public void Lists_every_catalog_kind()
    {
        using var catalog = JsonDocument.Parse(NodeSchema.GenerateCatalog());
        foreach (var entry in catalog.RootElement.EnumerateArray())
        {
            var kind = entry.GetProperty("kind").GetString()!;
            var page = GalleryPages.Find(kind);
            Assert.True(page is not null, $"The UI Gallery has no page for '{kind}'.");
            Assert.Equal(entry.GetProperty("isNeuron").GetBoolean() ? GalleryPages.ControlNeuron : GalleryPages.DataNode, page.Group);
        }
    }
}
