using DigitalBrain.Assistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.OS.Tests.Unit;

// Branding is configuration, so these facts pin the host's config to the sections the modules
// actually bind: AssistantModule reads the top-level "Assistant" section, FlutterModule binds
// its options from DigitalBrain:Modules:flutter:Options. Keys that bind to nothing are a
// silent branding regression.
public sealed class BrandingFacts
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException($"Repository root (DigitalBrain.slnx) was not found above {AppContext.BaseDirectory}.");
    }

    private static string HostPath(string file)
        => Path.Combine(RepositoryRoot, "src", "DigitalBrain", "OS", "DigitalBrain.OS", file);

    [Fact]
    public void TheHostAppSettingsCarryTheAssistantBrandingWhereTheModuleBindsIt()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(HostPath("appsettings.json")).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AssistantOptions>().BindConfiguration("Assistant");
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AssistantOptions>>().Value;
        Assert.Equal("IntoChat assistant", options.DisplayName);
        Assert.Contains("customer_research", options.Instructions);
    }

    [Fact]
    public void TheHostAppSettingsCarryTheShellTitleWhereTheFlutterModuleBindsIt()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(HostPath("appsettings.json")).Build();
        var options = new DigitalBrain.Flutter.FlutterModuleOptions();
        configuration.GetSection(DigitalBrain.DigitalBrainConfiguration.ModuleOptionsSection("flutter")).Bind(options);
        Assert.Equal("IntoChat", options.AssistantTitle);
    }
}
