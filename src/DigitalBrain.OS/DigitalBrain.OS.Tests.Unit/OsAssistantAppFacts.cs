using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsAssistantAppFacts
{
    [Fact]
    public void TheShippedAssistantDeclaresItsOperationsIconAndOneTestPerScenario()
    {
        var bundle = new EmbeddedShippedAppSource(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os").Load()
            .Single(app => app.Name == "assistant");

        Assert.Equal(["open", "send", "transcript", "new"], bundle.Manifest.Operations.Select(operation => operation.Name));
        Assert.Equal("icon.svg", bundle.Manifest.Icon);
        Assert.Contains("icon.svg", bundle.Files.Keys);
        Assert.Contains("synapses/surface.cs", bundle.Files.Keys);
        Assert.Contains("synapses/chat.cs", bundle.Files.Keys);
        var scenarios = bundle.Spec.Split('\n').Where(line => line.StartsWith("## Scenario:", StringComparison.Ordinal))
            .Select(line => line["## Scenario:".Length..].Trim()).ToArray();
        Assert.Equal(6, scenarios.Length);
        Assert.All(scenarios, scenario => Assert.Contains($"Scenario(\"{scenario}\"", bundle.Files[AppFiles.TestsPath], StringComparison.Ordinal));
    }
}
