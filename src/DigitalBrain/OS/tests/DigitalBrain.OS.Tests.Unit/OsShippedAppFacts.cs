using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsShippedAppFacts
{
    private static readonly EmbeddedShippedAppSource Source = new(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os");

    [Fact]
    public void TheOperatingSystemShipsSettingsAndAssistant()
    {
        Assert.Equal(["assistant", "settings"], Source.Load().Select(app => app.Package.Name).Order());
        Assert.All(Source.Load(), app => Assert.Equal("os", app.Package.Owner));
    }

    [Fact]
    public void AddOperatingSystemAppsNamesItsOwnManifestAndADeploymentOnlyAdds()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps(os => os.Apps.Add("intochat/customer-researcher"));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
        Assert.Equal(["os/settings", "os/assistant", "intochat/customer-researcher"], options.Apps);
        Assert.Equal(PackageId.Create("os", "settings"), provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
    }

    [Fact]
    public void ADistributionRegisteredAfterTheOsOverridesTheBuiltInSettingsPackage()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps();
        services.Configure<BuiltInSettingsOptions>(options => options.Package = PackageId.Create("acme", "settings"));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(PackageId.Create("acme", "settings"), provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
    }
}
