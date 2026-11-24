namespace IntoChat.OS.Architecture.Tests.Unit;

// The modules cut out of the catalog (src/Modules/Catalog) stand beside it, not on it: each reaches the catalog through its
// contracts assembly only, and the catalog's implementation never reaches back into them. Both the project references and the
// references the compiler wrote into the built assemblies are checked, so a reference that arrives through another project counts.
public sealed class CatalogSplitFacts
{
    private const string Catalog = "IntoChat.OS.Modules.Catalog";

    // Grows by one name with each module that leaves the catalog.
    private static readonly string[] SplitModules =
    [
        "IntoChat.OS.Modules.Runs",
        "IntoChat.OS.Modules.Authoring",
        "IntoChat.OS.Modules.Diagnostics",
    ];

    [Fact]
    public void ASplitModuleNeverReferencesTheCatalogImplementation()
    {
        var offenders = SplitModules
            .Where(module => Repository.References(Repository.ProjectNamed(module).Path).Contains(Catalog, StringComparer.Ordinal)
                || Compiled(module).Contains(Catalog, StringComparer.Ordinal))
            .ToArray();
        Assert.True(offenders.Length == 0, $"Modules split from the catalog reference {Catalog} (use {Catalog}.Contracts):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheCatalogImplementationNeverReferencesASplitModule()
    {
        var project = Repository.References(Repository.ProjectNamed(Catalog).Path).ToArray();
        var compiled = Compiled(Catalog);
        var offenders = SplitModules
            .Where(module => project.Contains(module, StringComparer.Ordinal) || compiled.Contains(module, StringComparer.Ordinal))
            .ToArray();
        Assert.True(offenders.Length == 0, $"{Catalog} references modules that were split from it:\n" + string.Join('\n', offenders));
    }

    private static string[] Compiled(string assembly)
        => [.. System.Reflection.Assembly.Load(assembly).GetReferencedAssemblies().Select(reference => reference.Name).OfType<string>()];
}
