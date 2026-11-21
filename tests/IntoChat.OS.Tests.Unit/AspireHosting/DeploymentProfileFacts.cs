using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void TheLocalTopologyHasAnEndpointAndOptionalChatOverride()
    {
        var ai = DeploymentProfile.LocalAi(model: null);
        Assert.Equal(IntoChatOSNames.LocalOllamaEndpoint, ai.Ollama.Endpoint);
        Assert.Empty(ai.Roles.Device);

        var overridden = DeploymentProfile.LocalAi(model: "gemma4:e2b");
        Assert.Equal("gemma4:e2b", overridden.Roles.Device[IntoChat.OS.Modules.AI.Models.Role.Chat]);

        // Standalone Kernel local topology (via AIClients PostConfigure) sets the Ollama endpoint
        // and maps IntoChatOS:Local:Model onto Roles.Device[Chat].
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                [IntoChatOSNames.LocalModelKey] = "gemma4:e2b",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<IntoChat.OS.Modules.AI.AIOptions>();
        IntoChat.OS.Modules.AI.AIClients.Add(services);

        using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IntoChat.OS.Modules.AI.AIOptions>>().Value;
        Assert.Equal(IntoChatOSNames.LocalOllamaEndpoint, configured.Ollama.Endpoint);
        Assert.Equal("gemma4:e2b", configured.Roles.Device[IntoChat.OS.Modules.AI.Models.Role.Chat]);
    }
}
