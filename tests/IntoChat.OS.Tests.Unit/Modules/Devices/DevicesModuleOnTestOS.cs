using DigitalBrain.Modules.Configuration;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class DevicesModuleOnTestOS : ModuleOnTestOSFacts<DevicesModule>;

public sealed class DevicesOptionsFacts
{
    [Fact]
    public void OptionsValidateUrls()
    {
        var options = new DevicesOptions();
        options.Attach["x"] = new AttachedRuntimeOptions { Url = "file:///c" };

        var error = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("x", error.Message, StringComparison.Ordinal);

        var cred = new DevicesOptions();
        cred.Ollama.Endpoint = "http://user:pass@localhost:11434";
        Assert.Throws<ArgumentException>(cred.Validate);

        var badName = new DevicesOptions();
        badName.Attach["Bad_Name!"] = new AttachedRuntimeOptions { Url = "http://localhost:1234/v1" };
        Assert.Throws<ArgumentException>(badName.Validate);

        var ok = new DevicesOptions();
        ok.Attach["lm-studio"] = new AttachedRuntimeOptions { Url = "http://localhost:1234/v1" };
        ok.Validate();
    }

    [Fact]
    public void TheOldAiOllamaEndpointIsRead()
    {
        ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AIKeys.OllamaEndpoint] = "http://127.0.0.1:22434",
            })
            .Build();
        var services = new ServiceCollection();
        DevicesModule.ConfigureServices(services, config);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IOptions<DevicesOptions>>().Value;
        Assert.Equal("http://127.0.0.1:22434", resolved.Ollama.Endpoint);
    }

    [Fact]
    public void ExplicitDevicesEndpointOverridesLegacyAiKey()
    {
        ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
        var devicesKey = ModuleConfiguration.ModuleOptionsSection(DevicesModule.ManifestName) + ":Ollama:Endpoint";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AIKeys.OllamaEndpoint] = "http://127.0.0.1:22434",
                [devicesKey] = "http://127.0.0.1:33434",
            })
            .Build();
        var services = new ServiceCollection();
        DevicesModule.ConfigureServices(services, config);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IOptions<DevicesOptions>>().Value;
        Assert.Equal("http://127.0.0.1:33434", resolved.Ollama.Endpoint);
    }
}
