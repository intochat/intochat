using DigitalBrain.Aspire.Server;
using DigitalBrain.Contracts;
using DigitalBrain.OS;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsCatalogFacts
{
    // The OS csproj references modules by glob; this guards its exclude patterns against
    // dropping a module the catalog composes (Playwright is the one nothing else pulls in).
    [Fact]
    public void EveryCatalogModuleShipsWithTheRuntime()
    {
        using var dependencies = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DigitalBrain.OS.deps.json")));
        var libraries = dependencies.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var module in OsCatalog.Modules)
        {
            Assert.Contains(module.GetType().Assembly.GetName().Name!, libraries);
        }
    }

    [Fact]
    public void CatalogIdsAreUnique()
    {
        var ids = OsCatalog.Modules.Select(module => module.Id.ToString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_two_catalog_modules_share_an_id_even_by_case()
    {
        var ids = OsCatalog.Modules.Select(m => m.Id.ToString().ToLowerInvariant()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Registering_two_modules_whose_ids_differ_only_by_case_is_a_duplicate()
    {
        var server = new DigitalBrainServerBuilder().AddModule(new CaseModuleA());
        Assert.Throws<InvalidOperationException>(() => server.AddModule(new CaseModuleB()));
    }

    private sealed class CaseOptions : IModuleOptions { public void Validate() { } }
    private sealed class CaseModuleA() : Module<CaseOptions>(new("case-id")) { public override void Configure(Orleans.Hosting.ISiloBuilder silo) { } }
    private sealed class CaseModuleB() : Module<CaseOptions>(new("CASE-ID")) { public override void Configure(Orleans.Hosting.ISiloBuilder silo) { } }
}
