using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsSelfImproveAppFacts
{
    [Fact]
    public void TheShippedSelfImproveAppDeclaresItsOperationsAndOneSynapsePerReaction()
    {
        var bundle = new EmbeddedShippedAppSource(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os").Load()
            .Single(app => app.Name == "selfimprove");
        Assert.Equal(["open", "request"], bundle.Manifest.Operations.Select(operation => operation.Name));
        Assert.Equal("icon.svg", bundle.Manifest.Icon);
        Assert.Contains("lib/SelfImprove.cs", bundle.Files.Keys);
        Assert.Equal(["synapses/failed.cs", "synapses/healthy.cs", "synapses/open.cs", "synapses/request.cs", "synapses/verified.cs"],
            bundle.Files.Keys.Where(file => file.StartsWith("synapses/", StringComparison.Ordinal)).Order());
        Assert.Contains("## Scenario:", bundle.Spec, StringComparison.Ordinal);
    }
}