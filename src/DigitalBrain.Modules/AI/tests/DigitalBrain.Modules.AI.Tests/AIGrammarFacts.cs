using DigitalBrain.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class AIGrammarFacts
{
    [Fact]
    public void ModuleOptionsSectionBindsTheOptions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DigitalBrain:Modules:ai:Options:Default:Model"] = "Fable5" }).Build();

        Assert.Equal("Fable5", AIOptionsRuntime.Read(configuration).Default.Model);
    }
}
