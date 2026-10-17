using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsCalculatorAppFacts
{
    [Fact]
    public void TheCalculatorDeclaresAClearButtonAndTheSynapseThatAnswersIt()
    {
        var bundle = SolutionProjects.FeaturesOf("os")
            .Single(app => app.Name == "calculator");

        Assert.Equal(["open", "clear", "add"], bundle.Manifest.Operations.Select(operation => operation.Name));
        var clear = Assert.Single(bundle.Manifest.Toolbar!);
        Assert.Equal(("clear", "button", "backspace", "Clear"), (clear.Operation, clear.Kind, clear.Icon, clear.Label));
        Assert.Contains("clear.cs", bundle.Files.Keys);
    }
}
