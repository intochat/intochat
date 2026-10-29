using DigitalBrain.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The Apps module, on TestOS, with Features unset: Development resolves features/os by walking to Intochat.OS.slnx.
public sealed class ShippedScriptCatalogFacts
{
    private static readonly string[] Scripts =
    [
        "brainview", "calculator", "circle", "circleroom", "datavisualizer", "engineer",
        "filmcraft", "friends", "ino", "memory", "photocraft", "router", "selfimprove", "usage",
    ];

    [Fact]
    public async Task TheCatalogListsEveryShippedScriptAndFiles()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton<IHostEnvironment>(new DevelopmentHost());
        AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
        await using var os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        os.Register(typeof(AppsModule).Assembly);

        var catalog = os.Services.GetRequiredService<FeatureCatalog>();
        var listings = await catalog.Listings();
        var names = listings.Select(listing => (listing.Publisher, listing.Name)).ToHashSet();

        Assert.EndsWith(Path.Combine("features", "os"), catalog.Directory!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.All(Scripts, name => Assert.Contains(("os", name), names));
        Assert.Contains(("os", "files"), names);
        Assert.Equal(Scripts.Length, listings.Count(listing => listing.Publisher == "os" && Scripts.Contains(listing.Name)));
        Assert.DoesNotContain(("os", "apps"), names);
        Assert.IsType<FeatureOrigin.Folder>(await catalog.Origin("os", "calculator"));
        foreach (var compiled in new[] { "files", "workspace", "desktop", "settings" })
        {
            var origin = Assert.IsType<FeatureOrigin.Module>(await catalog.Origin("os", compiled));
            Assert.Same(typeof(AppsModule).Assembly, origin.Assembly);
        }
    }

    private sealed class DevelopmentHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ShippedScriptCatalogFacts";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
