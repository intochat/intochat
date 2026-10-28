using System.Reflection;
using IntoChat.OS;
using IntoChat.OS.Kernel;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsCatalogFacts
{
    // The in-process OS (OsTestBrain) is the catalog minus the modules that need a container.
    // The Windows edge is not in the catalog at all: only WithWindowsEdge adds it.
    [Fact]
    public void InProcessModulesIsModulesMinusTheContainerBackedOnes()
    {
        string[] containerBacked = ["artcraft", "clickhouse", "postgres", "qdrant", "supabase"];
        string[] cloudOnly = ["auth", "circle-server"];
        Assert.Equal(containerBacked, OperatingSystemModules.Modules.Where(module => module.RequiresContainer).Select(module => module.Id.Value).Order(StringComparer.Ordinal));
        var expected = OperatingSystemModules.Modules.Select(module => module.Id.Value)
            .Except(containerBacked, StringComparer.OrdinalIgnoreCase)
            .Except(cloudOnly, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, OperatingSystemModules.InProcessModules.Select(module => module.Id.Value));
        Assert.All(cloudOnly, id => Assert.Contains(OperatingSystemModules.Modules, module => module.Id.Value == id && !module.LoadsUnder("local") && module.LoadsUnder("cloud")));
        Assert.All(containerBacked, id => Assert.Contains(OperatingSystemModules.Modules, module => module.Id.Value == id));
        Assert.DoesNotContain(OperatingSystemModules.Modules, module => module.Id.Value == "windows");
        Assert.Equal([.. OperatingSystemModules.Modules.Select(module => module.Id.Value), "windows"], OperatingSystemModules.WithWindowsEdge().Select(module => module.Id.Value));
    }

    // The OS csproj references modules by glob; this guards its exclude patterns against
    // dropping a module the catalog composes (Playwright is the one nothing else pulls in).
    [Fact]
    public void EveryCatalogModuleShipsWithTheRuntime()
    {
        using var dependencies = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "IntoChat.OS.Kernel.deps.json")));
        var libraries = dependencies.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var module in OperatingSystemModules.Modules)
        {
            Assert.Contains(module.Type.Assembly.GetName().Name!, libraries);
        }
        // The core vocabulary arrives as DigitalBrain (CORE-02), by project reference into framework/ since 06-00.
        Assert.Contains("DigitalBrain", libraries);
        Assert.Equal("project", dependencies.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(library => library.Name.Split('/')[0] == "DigitalBrain").Value.GetProperty("type").GetString());
    }

    // The MCP host reads a neuron's contract from its own loaded assemblies; a catalog
    // module whose Contracts project it does not reference fails every call with TypeLoadException.
    [Fact]
    public void TheMcpHostCarriesEveryCatalogModulesContracts()
    {
        var projects = System.Xml.Linq.XDocument.Load(Path.Combine(SolutionProjects.Root, "Intochat.OS.slnx"))
            .Descendants("Project").Select(project => Path.GetFileNameWithoutExtension(project.Attribute("Path")!.Value))
            .ToHashSet(StringComparer.Ordinal);
        var mcp = File.ReadAllText(Path.Combine(SolutionProjects.DirectoryOf("IntoChat.OS.Mcp"), "IntoChat.OS.Mcp.csproj"));
        foreach (var module in OperatingSystemModules.WithWindowsEdge())
        {
            var assembly = module.Type.Assembly.GetName().Name;
            var contracts = assembly + ".Contracts";
            // A module that hosts its own contracts has no Contracts project: the host must reference the module itself.
            if (IntoChat.OS.Client.Runtime.ContractsHost.Declares(module.Type.Assembly))
            {
                Assert.True(mcp.Contains("/" + assembly + ".csproj", StringComparison.Ordinal), assembly);
                continue;
            }
            if (!projects.Contains(contracts)) { continue; }
            // The host references every module Contracts project by glob; referencing the module project itself carries its contracts too (Tools).
            Assert.True(mcp.Contains("IntoChat.OS.Modules.*.Contracts.csproj", StringComparison.Ordinal) || mcp.Contains("/" + assembly + ".csproj", StringComparison.Ordinal), contracts);
        }
    }

    [Fact]
    public void TheOperatingSystemComposesTheAspireModule()
        => Assert.Contains(OperatingSystemModules.Modules, module => module.Id.ToString() == "aspire");

    [Fact]
    public void CatalogIdsAreUnique()
    {
        var ids = OperatingSystemModules.Modules.Select(module => module.Id.ToString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // Orleans is first (Order); the rest follow by ordinal id.
    [Fact]
    public void OrleansComesFirstAndTheRestAreOrdinal()
    {
        var ids = OperatingSystemModules.Modules.Select(module => module.Id.Value).ToArray();
        Assert.Equal("orleans", ids[0]);
        Assert.Equal(ids.Skip(1).Order(StringComparer.Ordinal), ids.Skip(1));
    }

    // Discovery reads the module assemblies the OS ships; a Module type found there that is neither
    // cataloged nor an explicit exclusion means the discovery scope or the exclusion list is wrong.
    [Fact]
    public void EveryModuleTypeIsCatalogedOrExcluded()
    {
        using var dependencies = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "IntoChat.OS.Kernel.deps.json")));
        var cataloged = OperatingSystemModules.Modules.Select(module => module.Type).ToHashSet();
        var shipped = dependencies.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name.Split('/')[0])
            .Where(name => name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal));
        foreach (var name in shipped)
        {
            System.Reflection.Assembly assembly;
            try { assembly = System.Reflection.Assembly.Load(name); }
            catch (FileNotFoundException) { continue; }
            foreach (var type in assembly.GetExportedTypes().Where(type => !type.IsAbstract
                && typeof(DigitalBrain.Module).IsAssignableFrom(type)
                && type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is not null
                && type.GetConstructor(Type.EmptyTypes) is not null))
            {
                Assert.True(cataloged.Contains(type) || OperatingSystemModules.IsExcluded(type), type.FullName);
            }
        }
    }

    // The AppHost references hosting projects by glob; a hosting project in the solution whose assembly
    // the host process cannot load would leave its module unhosted, so it fails here instead of silently.
    [Fact]
    public void EveryAspireHostingProjectIsReachableFromTheAppHost()
    {
        var onDisk = System.Xml.Linq.XDocument.Load(Path.Combine(SolutionProjects.Root, "Intochat.OS.slnx"))
            .Descendants("Project").Select(project => project.Attribute("Path")!.Value.Replace('\\', '/'))
            .Where(path => path.Contains("/IntoChat.OS.Modules/", StringComparison.Ordinal) && !path.Contains("/tests/", StringComparison.Ordinal)
                && path.EndsWith(".Aspire.Hosting.csproj", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
        Assert.NotEmpty(onDisk);
        foreach (var name in onDisk)
        {
            var exception = Record.Exception(() => System.Reflection.Assembly.Load(name));
            Assert.True(exception is null, name + " is not reachable from the AppHost references");
        }
    }
}
