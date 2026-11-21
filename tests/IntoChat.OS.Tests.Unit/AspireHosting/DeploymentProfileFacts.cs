using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentProfileFacts
{
    [Theory]
    [InlineData(null, "local")]
    [InlineData(" Local ", "local")]
    [InlineData("local-cloud", "local+cloud")]
    [InlineData("CLOUD", "cloud")]
    [InlineData("mars", null)]
    public void TopologyHasOneCanonicalSpelling(string? value, string? canonical)
        => Assert.Equal(canonical, IntoChatOSNames.CanonicalTopology(value));

    [Fact]
    public void AnExplicitEnabledBeatsTheLocalDefault()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [LocalRunDefaults.EnabledKey("postgres")] = "true" });
        LocalRunDefaults.Apply(configuration);

        Assert.True(LocalRunDefaults.IsEnabled(configuration, "postgres"));
        Assert.False(LocalRunDefaults.IsEnabled(configuration, "qdrant"));
    }

    [Fact]
    public void CloudKeepsEveryModuleOn()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [IntoChatOSNames.TopologyKey] = "cloud" });
        LocalRunDefaults.Apply(configuration);

        Assert.True(LocalRunDefaults.IsEnabled(configuration, "postgres"));
    }

    [Fact]
    public void OnlySettingsThatDifferFromTheDefaultAreProjected()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalRunDefaults.EnabledKey("postgres")] = "true",
            [LocalRunDefaults.EnabledKey("qdrant")] = "false",
            [LocalRunDefaults.EnabledKey("github")] = "false",
        });

        Assert.Equal(
            [LocalRunDefaults.EnabledKey("github"), LocalRunDefaults.EnabledKey("postgres")],
            DeploymentProfile.ExplicitModuleSettings(configuration, IntoChatOSNames.TopologyLocal));
    }
}
