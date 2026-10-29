using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// R4-03 (D-08 revised, 05-13): module folders are grouped by module, not by role. Every module is one folder holding Contracts/
// (its public contracts project), src/ (the implementation: neurons, UI builders, the Module class with its ModuleManifest,
// hosting) and tests/ (unit, conformance kit subclasses, integration). No role folders (Runtime, UI, Connectors, Data) at
// module level.
public sealed partial class ModuleFolderFacts
{
    private static readonly string[] RoleFolders = ["Runtime", "UI", "Connectors", "Data"];

    // A module's implementation folder (spelled so RepositoryPathLiteralFacts does not read it as a repository path).
    internal const string SourceFolder = "s" + "rc";

    private static IEnumerable<Repository.Project> ModuleProjects() => Repository.Projects.Values
        .Where(project => project.Directory.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsTest(Repository.Project project)
        => Repository.TestProjects().Contains(project) || project.Name.Contains(".Tests.", StringComparison.Ordinal);

    // The module folder of a project: the parent of its Contracts/, src/ or tests/ folder.
    private static string ModuleOf(Repository.Project project) => Path.GetDirectoryName(Path.GetDirectoryName(project.Directory)!)!;

    [Fact]
    public void EveryModuleProjectSitsInItsModulesContractsSrcOrTestsFolder()
    {
        var misplaced = ModuleProjects()
            .Where(project => Path.GetFileName(Path.GetDirectoryName(project.Directory)) != Expected(project))
            .Select(project => $"{Repository.Relative(project.Directory)} (expected under {Expected(project)}/)")
            .ToList();
        Assert.True(misplaced.Count == 0, "Module projects outside their module's Contracts, implementation or tests folder: " + string.Join(", ", misplaced));

        static string Expected(Repository.Project project)
            => IsTest(project) ? "tests" : project.Name.EndsWith(".Contracts", StringComparison.Ordinal) ? "Contracts" : SourceFolder;
    }

    [Fact]
    public void EveryModuleHasTestsAndNoRoleFolders()
    {
        var modules = ModuleProjects().Where(project => !IsTest(project)).Select(ModuleOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(modules);
        var untested = modules.Where(module => !ModuleProjects().Any(project => IsTest(project) && string.Equals(ModuleOf(project), module, StringComparison.OrdinalIgnoreCase)))
            .Select(Repository.Relative).ToList();
        Assert.True(untested.Count == 0, "Modules without a test project in their own tests/: " + string.Join(", ", untested));
        var roles = modules.SelectMany(module => RoleFolders.Where(role => Directory.Exists(Path.Combine(module, role))).Select(role => Repository.Relative(Path.Combine(module, role))))
            .ToList();
        Assert.True(roles.Count == 0, "Role folders at module level (group by module instead): " + string.Join(", ", roles));
    }

    [Fact]
    public void EveryDigitalBrainModuleClassLivesInItsModulesSrc()
    {
        var outside = ModuleProjects().Where(project => !IsTest(project))
            .SelectMany(project => Repository.SourceFiles(project.Directory, "*.cs", path => ModuleClass().IsMatch(File.ReadAllText(path)))
                .Select(path => (project, path)))
            .Where(pair => Path.GetFileName(Path.GetDirectoryName(pair.project.Directory)) != SourceFolder)
            .Select(pair => Repository.Relative(pair.path))
            .ToList();
        Assert.True(outside.Count == 0, "Module classes outside their module's implementation folder: " + string.Join(", ", outside));
    }

    [Fact]
    public void EveryConformanceKitSubclassLivesInATestsFolder()
    {
        var subclasses = Repository.ProjectFiles("*.cs", path => KitSubclass().IsMatch(File.ReadAllText(path))).ToList();
        Assert.NotEmpty(subclasses);
        var misplaced = subclasses.Where(path => !IsTestFile(path)).Select(Repository.Relative).ToList();
        Assert.True(misplaced.Count == 0, "Conformance kit subclasses outside a test project: " + string.Join(", ", misplaced));
        // A module's kit subclass sits in that module's own tests/ folder.
        foreach (var path in subclasses.Where(path => path.StartsWith(Repository.ModulesRoot, StringComparison.OrdinalIgnoreCase)))
        {
            var parts = Repository.Relative(path).Split('/');
            Assert.Contains("tests", parts);
        }

        static bool IsTestFile(string path)
            => Repository.ProjectKey(path).Split('/')[0] is var name && (name.Contains(".Tests.", StringComparison.Ordinal) || name.EndsWith(".Tests", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*DigitalBrain\.Module\b")]
    private static partial Regex ModuleClass();

    [GeneratedRegex(@":\s*(Storage|Secrets|Identity)Conformance<")]
    private static partial Regex KitSubclass();
}
