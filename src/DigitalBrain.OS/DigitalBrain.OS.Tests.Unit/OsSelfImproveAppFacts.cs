using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsSelfImproveAppFacts
{
    [Fact]
    public void TheShippedSelfImproveAppDeclaresItsOperationsAndOneSynapsePerReaction()
    {
        var bundle = SolutionProjects.FeaturesOf("os")
            .Single(app => app.Name == "selfimprove");
        Assert.Equal(["open", "request"], bundle.Manifest.Operations.Select(operation => operation.Name));
        Assert.Equal("icon.svg", bundle.Manifest.Icon);
        Assert.Equal(["SelfImprove.cs", "failed.cs", "healthy.cs", "list.cs", "open.cs", "request.cs", "verified.cs"],
            bundle.Files.Keys.Where(AppFiles.IsProgram).Order(StringComparer.Ordinal));
        Assert.Contains("## Scenario:", bundle.Spec, StringComparison.Ordinal);
    }
}