using DigitalBrain.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class AIGrammarFacts
{
    [Fact]
    public void LegacyAiSectionRefusesToStart()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DigitalBrain:AI:Default:Model"] = "Fable5" }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => AIOptionsRuntime.Read(configuration));
        Assert.Contains("DigitalBrain:Modules:ai:Options", error.Message);
    }

    [Fact]
    public void LegacyWorkspaceSectionRefusesToStart()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DigitalBrain:Workspace:RepositoryPath"] = "/repo" }).Build();

        Assert.Throws<InvalidOperationException>(() => AIOptionsRuntime.Read(configuration));
    }

    [Fact]
    public void ModuleOptionsSectionBindsTheOptions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DigitalBrain:Modules:ai:Options:Default:Model"] = "Fable5" }).Build();

        Assert.Equal("Fable5", AIOptionsRuntime.Read(configuration).Default.Model);
    }
}
