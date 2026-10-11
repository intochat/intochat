using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsAssistantAppFacts
{
    [Fact]
    public void TheShippedAssistantDeclaresItsOperationsIconAndSynapses()
    {
        var bundle = SolutionProjects.FeaturesOf("os")
            .Single(app => app.Name == "assistant");

        Assert.Equal(["open", "send", "new", "voice", "make"], bundle.Manifest.Operations.Select(operation => operation.Name));
        Assert.Equal("icon.svg", bundle.Manifest.Icon);
        Assert.Contains("icon.svg", bundle.Files.Keys);
        Assert.Contains("Assistant.cs", bundle.Files.Keys);
        Assert.Contains("open.cs", bundle.Files.Keys);
        Assert.Contains("send.cs", bundle.Files.Keys);
        Assert.Contains("new.cs", bundle.Files.Keys);
    }

    [Fact]
    public void TheAssistantToolbarIsATextFieldAndAVoiceButton()
    {
        var bundle = SolutionProjects.FeaturesOf("os")
            .Single(app => app.Name == "assistant");

        Assert.Equal([("send", "text", null), ("voice", "button", "mic")],
            bundle.Manifest.Toolbar!.Select(entry => (entry.Operation, entry.Kind, entry.Icon)));
    }

    [Fact]
    public void TheShippedAssistantDeclaresTheRouteGateSetting()
    {
        var bundle = SolutionProjects.FeaturesOf("os")
            .Single(app => app.Name == "assistant");

        var gate = Assert.Single(bundle.Manifest.Settings, setting => setting.Name == "RouteGate");
        Assert.Equal("0.8", gate.Default);
        Assert.Contains("## Scenario: A confident route answers without the language model", bundle.Spec, StringComparison.Ordinal);
    }
}
