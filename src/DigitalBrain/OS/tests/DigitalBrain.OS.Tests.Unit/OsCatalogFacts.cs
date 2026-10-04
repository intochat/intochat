using DigitalBrain.Contracts;
using DigitalBrain.OS;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsCatalogFacts
{
    [Fact]
    public void EveryCatalogModuleDeclaresItsIdOnTheType()
    {
        Assert.NotEmpty(OsCatalog.Modules);
        foreach (var module in OsCatalog.Modules)
        {
            Assert.True(module.IsDefined(typeof(ModuleIdAttribute), inherit: false),
                $"{module.FullName} carries no [ModuleId].");
        }
    }

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
            Assert.Contains(module.Assembly.GetName().Name!, libraries);
        }
    }

    [Fact]
    public void CatalogIdsAreUnique()
    {
        var ids = OsCatalog.Modules.Select(ModuleIdentity.Get).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
