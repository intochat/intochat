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
        services.AddOperatingSystemApps(os => os.Apps.Add(OsApp.Parse("intochat/customer-researcher")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
        Assert.Equal(["os/settings", "os/assistant", "intochat/customer-researcher"],
            options.Apps.Select(app => app.Package.ToString()));
        Assert.Equal(PackageId.Create("os", "settings"), provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
    }

    [Fact]
    public void EveryOsProgramAndTestsFileCompilesAgainstCurrentContracts()
    {
        foreach (var app in Source.Load())
        {
            var sources = new Dictionary<string, string> { [PackageContent.TestsPath] = app.Content.File(PackageContent.TestsPath)! };
            foreach (var (path, program) in app.Content.Programs()) { sources[path] = program; }
            var result = new DigitalBrain.Microsoft.CSharp.CSharpScriptCheck().Check(sources);
            Assert.True(result.Success, $"{app.Package}: {string.Join("; ", result.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}"))}");
        }
    }

    [Fact]
    public void TheOperatingSystemCompositionWiresItsAppsIntoTheHost()
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        DigitalBrain.Aspire.Server.ServerHostingExtensions.AddDigitalBrainServer(builder, server => server.AddOperatingSystem());
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal(PackageId.Create("os", "settings"), provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
        Assert.Contains(provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps,
            app => app.Package.ToString() == "os/settings");
    }

    [Fact]
    public void RegisteringTheOsAppsTwiceDoesNotDuplicateTheManifest()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps();
        services.AddOperatingSystemApps();
        using var provider = services.BuildServiceProvider();
        Assert.Equal(2, provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps.Count);
    }

    [Fact]
    public void AMalformedManifestEntryFailsAtParseNamingTheValue()
    {
        var package = Assert.ThrowsAny<Exception>(() => OsApp.Parse("os-settings"));
        Assert.Contains("os-settings", package.Message);
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
