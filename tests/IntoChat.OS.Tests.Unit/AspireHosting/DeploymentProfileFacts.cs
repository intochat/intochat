using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public void TheLocalTopologyHasAChatModelAndAnEndpoint()
    {
        var ai = DeploymentProfile.LocalAi(model: null);
        Assert.Equal(IntoChatOSNames.LocalOllamaEndpoint, ai.Ollama.Endpoint);
        Assert.Equal(nameof(IntoChat.OS.Modules.AI.Models.Ollama.IQwen35), ai.Default.Model);

        // Standalone Kernel local topology (via AIClients PostConfigure) gets the same chat model and Ollama endpoint.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<IntoChat.OS.Modules.AI.AIOptions>();
        IntoChat.OS.Modules.AI.AIClients.Add(services);

        using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntoChat.OS.Modules.AI.AIOptions>>().Value;
        Assert.Equal(IntoChatOSNames.LocalOllamaEndpoint, configured.Ollama.Endpoint);
        Assert.Equal(nameof(IntoChat.OS.Modules.AI.Models.Ollama.IQwen35), configured.Default.Model);
    }
}
