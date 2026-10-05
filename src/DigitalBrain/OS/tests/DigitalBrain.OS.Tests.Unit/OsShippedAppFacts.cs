using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsShippedAppFacts
{
    private static readonly EmbeddedShippedAppSource Source = new(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os");

    [Fact]
    public void TheOperatingSystemShipsSettings()
    {
        Assert.Equal(["settings"], Source.Load().Select(app => app.Name).Order());
    }

    [Fact]
    public void AddOperatingSystemAppsNamesItsOwnManifestAndADeploymentOnlyAdds()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps(os => os.Apps.Add(new("customer-researcher")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
        Assert.Equal(["settings", "customer-researcher"], options.Apps.Select(app => app.Name));
    }

    [Fact]
    public void EveryOsProgramAndTestsFileCompilesAgainstCurrentContracts()
    {
        foreach (var app in Source.Load())
        {
            var sources = new Dictionary<string, string>(app.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)));
            var result = new DigitalBrain.Microsoft.CSharp.CSharpScriptCheck().Check(sources);
            Assert.True(result.Success, $"{app.Name}: {string.Join("; ", result.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}"))}");
        }
    }

    [Fact]
    public void TheOperatingSystemCompositionWiresItsAppsIntoTheHost()
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        DigitalBrain.Aspire.Server.ServerHostingExtensions.AddDigitalBrainServer(builder, server => server.AddOperatingSystem());
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Contains(provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps, app => app.Name == "settings");
    }

    [Fact]
    public void RegisteringTheOsAppsTwiceDoesNotDuplicateTheManifest()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps();
        services.AddOperatingSystemApps();
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps);
    }
}
