using System.Text.RegularExpressions;
using DigitalBrain.Modules;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Module folders are grouped by module under src/Modules/<Name>/. Each module project is flattened directly
// into its module folder (with its Module class, ModuleManifest, neurons, and hosting). No role folders
// (Runtime, UI, Connectors, Data) at module level. Module tests sit under tests/IntoChat.OS.Tests.Unit/Modules/<Name>/
// or in the module's own tests/IntoChat.OS.Modules.<Name>.Tests.Unit project.
public sealed partial class ModuleFolderFacts
{
    private static readonly string[] RoleFolders = ["Runtime", "UI", "Connectors", "Data"];
    private static readonly string[] PlaceholderModules = [];

    // A module's implementation folder (spelled so RepositoryPathLiteralFacts does not read it as a repository path).
    internal const string SourceFolder = "s" + "rc";

    private static IEnumerable<Repository.Project> ModuleProjects() => Repository.Projects.Values
        .Where(project => project.Directory.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsModuleContractsProject(Repository.Project project)
        => project.Name.EndsWith(".Contracts", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(project.Directory), "Contracts", StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(project.Directory)), Repository.ModulesRoot, StringComparison.OrdinalIgnoreCase);

    private static bool IsModuleGeneratorsProject(Repository.Project project)
        => project.Name.EndsWith(".Generators", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(project.Directory), "Generators", StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(project.Directory)), Repository.ModulesRoot, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryModuleProjectSitsInItsModulesContractsSrcOrTestsFolder()
    {
        var misplaced = ModuleProjects()
            .Where(project => Path.GetDirectoryName(project.Directory) != Repository.ModulesRoot
                && !IsModuleContractsProject(project)
                && !IsModuleGeneratorsProject(project))
            .Select(project => Repository.Relative(project.Directory))
            .ToList();
        Assert.True(misplaced.Count == 0, "Module projects outside src/Modules/<Name>/: " + string.Join(", ", misplaced));
    }

    [Fact]
    public void EveryModuleHasTestsAndNoRoleFolders()
    {
        var modules = ModuleProjects()
            .Select(project => IsModuleContractsProject(project) || IsModuleGeneratorsProject(project) ? Path.GetDirectoryName(project.Directory)! : project.Directory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(modules);
        var unitTestsDir = Path.Combine(Repository.Root, "tests", "IntoChat.OS.Tests.Unit", "Modules");
        var untested = modules
            .Where(module => !PlaceholderModules.Contains(Path.GetFileName(module), StringComparer.OrdinalIgnoreCase))
            .Where(module => !Directory.Exists(Path.Combine(unitTestsDir, Path.GetFileName(module)))
                && !Repository.Projects.ContainsKey($"IntoChat.OS.Modules.{Path.GetFileName(module)}.Tests.Unit"))
            .Select(Repository.Relative).ToList();
        Assert.True(untested.Count == 0, "Modules without unit tests under tests/IntoChat.OS.Tests.Unit/Modules/: " + string.Join(", ", untested));
        var roles = modules.SelectMany(module => RoleFolders.Where(role => Directory.Exists(Path.Combine(module, role))).Select(role => Repository.Relative(Path.Combine(module, role))))
            .ToList();
        Assert.True(roles.Count == 0, "Role folders at module level (group by module instead): " + string.Join(", ", roles));
    }

    [Fact]
    public void EveryDigitalBrainModuleClassLivesInItsModulesSrc()
    {
        var outside = ModuleProjects()
            .SelectMany(project => Repository.SourceFiles(project.Directory, "*.cs", path => ModuleClass().IsMatch(File.ReadAllText(path)))
                .Select(path => (project, path)))
            .Where(pair => Path.GetDirectoryName(pair.project.Directory) != Repository.ModulesRoot)
            .Select(pair => Repository.Relative(pair.path))
            .ToList();
        Assert.True(outside.Count == 0, "Module classes outside their module's project folder: " + string.Join(", ", outside));
    }

    [Fact]
    public void EveryConformanceKitSubclassLivesInATestsFolder()
    {
        var subclasses = Repository.ProjectFiles("*.cs", path => KitSubclass().IsMatch(File.ReadAllText(path))).ToList();
        Assert.NotEmpty(subclasses);
        var misplaced = subclasses.Where(path => !IsTestFile(path)).Select(Repository.Relative).ToList();
        Assert.True(misplaced.Count == 0, "Conformance kit subclasses outside a test project: " + string.Join(", ", misplaced));
        foreach (var path in subclasses)
        {
            var parts = Repository.Relative(path).Split('/');
            Assert.Contains("tests", parts);
        }

        static bool IsTestFile(string path)
            => Repository.ProjectKey(path).Split('/')[0] is var name && (name.Contains(".Tests.", StringComparison.Ordinal) || name.EndsWith(".Tests", StringComparison.Ordinal) || name.Contains(".Tests", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*DigitalBrain\.Module\b")]
    private static partial Regex ModuleClass();

    [GeneratedRegex(@":\s*(Storage|Secrets|Identity)Conformance<")]
    private static partial Regex KitSubclass();
}
