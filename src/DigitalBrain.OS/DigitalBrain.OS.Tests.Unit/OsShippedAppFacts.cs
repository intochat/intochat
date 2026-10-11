using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsShippedAppFacts
{
    [Fact]
    public void TheOperatingSystemShipsSettings()
    {
        Assert.Equal(["assistant", "calculator", "circle", "datavisualizer", "files", "filmcraft", "photocraft", "router", "selfimprove", "settings"], SolutionProjects.FeaturesOf("os").Select(app => app.Name).Order());
    }

    [Fact]
    public void AddOperatingSystemAppsNamesItsOwnManifestAndADeploymentOnlyAdds()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps(os => os.Apps.Add(new("intochat", "customer-researcher")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
        Assert.Equal(["os/settings", "os/assistant", "os/router", "os/calculator", "os/datavisualizer", "os/selfimprove", "intochat/customer-researcher"], options.Apps.Select(app => app.Publisher + "/" + app.Name));
    }

    [Fact]
    public void TheOperatingSystemCompositionWiresItsAppsIntoTheHost()
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        DigitalBrain.Aspire.Server.ServerHostingExtensions.AddDigitalBrainServer(builder, server => server.AddOperatingSystem());
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Contains(provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps, app => app.Publisher == "os" && app.Name == "settings");
    }

    [Fact]
    public void RegisteringTheOsAppsTwiceDoesNotDuplicateTheManifest()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps();
        services.AddOperatingSystemApps();
        using var provider = services.BuildServiceProvider();
        Assert.Equal(6, provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps.Count);
    }
}
