using DigitalBrain.OS;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class LegacyConfigurationGuardFacts
{
    [Theory]
    [InlineData("DigitalBrain:Apps:ShipOnStartup")]
    [InlineData("DigitalBrain:ShippedApps:vendor:Path")]
    [InlineData("DigitalBrain:CSharp:EdgeUrl")]
    [InlineData("DigitalBrain:Microsoft:GitHub:App:AppId")]
    [InlineData("DigitalBrain:Microsoft:Aspire:BridgeKey")]
    [InlineData("DigitalBrain:Salesforce:OAuth:ConsumerKey")]
    [InlineData("DigitalBrain:Flutter:Hosting:Kind")]
    [InlineData("DigitalBrain:Time:MinimumReminderPeriod")]
    [InlineData("DigitalBrain:AI:Default:Model")]
    [InlineData("DigitalBrain:Workspace:RepositoryPath")]
    public void ARetiredSectionRefusesStartupNamingItsReplacement(string legacyKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [legacyKey] = "value" }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => LegacyConfigurationGuard.Check(configuration));
        Assert.Contains("DigitalBrain:Modules:", error.Message);
    }

    [Fact]
    public void TheMigratedGrammarPassesTheGuard()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DigitalBrain:Modules:ai:Options:Default:Model"] = "Fable5",
            ["DigitalBrain:Modules:apps:Shipped:intochat:Path"] = "/apps",
            ["DigitalBrain:Modules:time:Options:MinimumReminderPeriod"] = "00:00:01",
        }).Build();

        LegacyConfigurationGuard.Check(configuration);
    }
}
