using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsShippedAppFacts
{
    [Fact]
    public void TheOperatingSystemShipsSettings()
    {
        Assert.Equal(["apps", "brainview", "calculator", "circle", "circleroom", "datavisualizer", "desktop", "engineer", "files", "filmcraft", "friends", "ino", "memory", "photocraft", "router", "selfimprove", "settings", "usage", "workspace"], SolutionProjects.FeaturesOf("os").Select(app => app.Name).Order());
    }

    [Fact]
    public void TheAssistantOffersMakeForTheFocusedThing()
    {
        var assistant = Assert.Single(SolutionProjects.FeaturesOf("os"), app => app.Name == "ino");
        Assert.Contains(assistant.Manifest.Operations, operation => operation.Name == "make" && operation.Description.Contains("focus", StringComparison.Ordinal));
        Assert.True(assistant.Files.ContainsKey("make.cs"));
    }

    [Fact]
    public void AddOperatingSystemAppsNamesItsOwnManifestAndADeploymentOnlyAdds()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps(os => os.Apps.Add(new("acme", "example")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
        Assert.Equal(["os/settings", "os/apps", "os/router", "os/calculator", "os/filmcraft", "os/photocraft", "os/datavisualizer", "os/selfimprove", "os/memory", "os/ino", "os/brainview", "os/friends", "os/circle", "os/workspace", "os/desktop", "os/usage", "acme/example"], options.Apps.Select(app => app.Publisher + "/" + app.Name));
    }

    [Fact]
    public void TheOperatingSystemCompositionWiresItsAppsIntoTheHost()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        IntoChat.OS.Aspire.Server.ServerHostingExtensions.AddIntoChatOSServer(builder, server => server.AddOperatingSystem());
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
        Assert.Equal(16, provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps.Count);
    }
}
