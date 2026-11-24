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

    [Fact]
    public async Task AnAppWhoseRequiredNeuronTypeIsNotHostedIsExcludedFromBootSet()
    {
        var catalogWithoutPostgres = new FeatureCatalog(
            [typeof(IntoChat.OS.Apps.ShippedShellAppsModule).Assembly],
            isNeuronTypeHosted: type => type != "postgres.table");

        var availability = await catalogWithoutPostgres.Availability("os", "customer-researcher");
        var needs = Assert.IsType<AppAvailability.NeedsProvisioning>(availability);
        Assert.Contains("postgres.table", needs.MissingNeuronTypes);
        Assert.DoesNotContain(await catalogWithoutPostgres.BootSet(), item => item.Name == "customer-researcher");

        var catalogWithPostgres = new FeatureCatalog(
            [typeof(IntoChat.OS.Apps.ShippedShellAppsModule).Assembly],
            isNeuronTypeHosted: _ => true);

        Assert.IsType<AppAvailability.Ready>(await catalogWithPostgres.Availability("os", "customer-researcher"));
        Assert.Contains(await catalogWithPostgres.BootSet(), item => item.Name == "customer-researcher");
    }
}
