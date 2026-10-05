using DigitalBrain.OS;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class LegacyConfigurationGuardFacts
{
    [Theory]
    [InlineData("DigitalBrain:Capacity:Postgres:AdminConnection")]
    [InlineData("DigitalBrain:Modules:csharp:Deployment:RunTokenKey")]
    [InlineData("DigitalBrain:Modules:salesforce:OAuth:ConsumerSecret")]
    [InlineData("DigitalBrain:Modules:github:App:ClientSecret")]
    [InlineData("DigitalBrain:Integrations:gmail:ClientSecret")]
    [InlineData("DigitalBrain:Modules:supabase:Options:ConnectionString")]
    [InlineData("DigitalBrain:Modules:clickhouse:Options:ConnectionString")]
    public void RetiredCredentialChannelsFailWithoutEchoingValues(string key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "canary-secret" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => LegacyConfigurationGuard.Check(config));
        Assert.Contains("DigitalBrain:Integrations:", error.Message);
        Assert.DoesNotContain("canary-secret", error.Message);
    }

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
