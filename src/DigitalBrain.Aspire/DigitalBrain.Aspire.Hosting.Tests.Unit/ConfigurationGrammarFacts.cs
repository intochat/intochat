using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ConfigurationGrammarFacts
{
    [Fact]
    public void TheEnvironmentFormOfAKeySwapsEverySectionSeparatorForADoubleUnderscore()
        => Assert.Equal("A__B__C", DigitalBrainConfiguration.EnvironmentName("A:B:C"));
}
