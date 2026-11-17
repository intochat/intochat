namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// The catalog is the compiled apps the module assemblies ship; nothing is read from a folder.
public sealed class FeatureCatalogFacts
{
    [Fact]
    public async Task TheCatalogListsTheCompiledAppsOfItsAssemblies()
    {
        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly]);

        var origin = Assert.IsType<FeatureOrigin.ModuleSource>(await catalog.Origin("os", "files"));
        Assert.Same(typeof(AppsModule).Assembly, origin.Assembly);
        Assert.Contains(await catalog.Listings(), listing => listing.Publisher == "os" && listing.Name == "files");
        Assert.Empty(await catalog.Failures());
    }

    [Fact]
    public async Task AnAssemblyListedTwiceShipsItsAppsOnce()
    {
        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(AppsModule).Assembly]);

        Assert.Single(await catalog.Listings(), listing => listing.Name == "files");
    }

    [Fact]
    public async Task ACatalogWithoutAssembliesHasNoApps()
    {
        var catalog = new FeatureCatalog([]);

        Assert.Empty(await catalog.Listings());
        Assert.Null(await catalog.Find("os", "files"));
    }
}
