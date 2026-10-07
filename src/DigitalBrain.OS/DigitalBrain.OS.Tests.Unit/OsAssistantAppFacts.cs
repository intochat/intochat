using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsAssistantAppFacts
{
    [Fact]
    public void TheShippedAssistantDeclaresItsOperationsIconAndSynapses()
    {
        var bundle = new EmbeddedShippedAppSource(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os").Load()
            .Single(app => app.Name == "assistant");

        Assert.Equal(["open", "send", "transcript", "new"], bundle.Manifest.Operations.Select(operation => operation.Name));
        Assert.Equal("icon.svg", bundle.Manifest.Icon);
        Assert.Contains("icon.svg", bundle.Files.Keys);
        Assert.Contains("synapses/surface.cs", bundle.Files.Keys);
        Assert.Contains("synapses/chat.cs", bundle.Files.Keys);
    }
}
