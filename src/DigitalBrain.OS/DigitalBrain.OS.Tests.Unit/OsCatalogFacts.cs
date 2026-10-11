using DigitalBrain;
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

    // The MCP host reads a neuron's contract from its own loaded assemblies; a catalog
    // module whose Contracts project it does not reference fails every call with TypeLoadException.
    [Fact]
    public void TheMcpHostCarriesEveryCatalogModulesContracts()
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(ThisFile())!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DigitalBrain.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var modules = Path.Combine(root.FullName, "src", "DigitalBrain.Modules");
        var mcp = File.ReadAllText(Path.Combine(root.FullName, "src", "DigitalBrain.Mcp", "DigitalBrain.Mcp", "DigitalBrain.Mcp.csproj"));
        foreach (var module in OsCatalog.Modules)
        {
            var assembly = module.GetType().Assembly.GetName().Name;
            var contracts = assembly + ".Contracts";
            if (Directory.GetFiles(modules, contracts + ".csproj", SearchOption.AllDirectories).Length == 0) { continue; }
            // Referencing the module project itself carries its contracts too (Tools).
            Assert.True(mcp.Contains(contracts + ".csproj", StringComparison.Ordinal) || mcp.Contains("/" + assembly + ".csproj", StringComparison.Ordinal), contracts);
        }
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string source = "") => source;

    [Fact]
    public void TheOperatingSystemComposesTheAspireModule()
        => Assert.Contains(OsCatalog.Modules, module => module.Id.ToString() == "aspire");

    [Fact]
    public void CatalogIdsAreUnique()
    {
        var ids = OsCatalog.Modules.Select(module => module.Id.ToString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
