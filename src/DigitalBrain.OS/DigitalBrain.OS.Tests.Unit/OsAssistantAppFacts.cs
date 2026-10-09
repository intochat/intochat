using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsAssistantAppFacts
{
    [Fact]
    public void TheShippedAssistantDeclaresItsOperationsIconAndSynapses()
    {
        var bundle = new EmbeddedShippedAppSource(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os").Load()
            .Single(app => app.Name == "assistant");

        Assert.Equal(["open", "send", "new"], bundle.Manifest.Operations.Select(operation => operation.Name));
        Assert.Equal("icon.svg", bundle.Manifest.Icon);
        Assert.Contains("icon.svg", bundle.Files.Keys);
        Assert.Contains("lib/Assistant.cs", bundle.Files.Keys);
        Assert.Contains("synapses/open.cs", bundle.Files.Keys);
        Assert.Contains("synapses/send.cs", bundle.Files.Keys);
        Assert.Contains("synapses/new.cs", bundle.Files.Keys);
    }

    [Fact]
    public void TheShippedAssistantDeclaresTheRouteGateSetting()
    {
        var bundle = new EmbeddedShippedAppSource(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os").Load()
            .Single(app => app.Name == "assistant");

        var gate = Assert.Single(bundle.Manifest.Settings, setting => setting.Name == "RouteGate");
        Assert.Equal("0.8", gate.Default);
        Assert.Contains("## Scenario: A confident route answers without the language model", bundle.Spec, StringComparison.Ordinal);
    }
}
