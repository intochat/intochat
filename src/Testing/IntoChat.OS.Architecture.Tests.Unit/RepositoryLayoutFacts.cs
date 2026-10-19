using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The solution mirrors the disk. A project at src/A/B/<Name>/<Name>.csproj sits in the solution
// folder /A/B/ with the "IntoChat.OS." prefix dropped from each segment that carries it
// (src/IntoChat.OS.Aspire → /Aspire/, src/IntoChat.OS.Modules/AI → /Modules/AI/); a project
// directly under src has no folder. Module tests live under their module's tests/ directory and
// are grouped either with the module or in a /tests/ subfolder; ring tests sit beside their ring.
public sealed class RepositoryLayoutFacts
{
    private const string Prefix = "IntoChat.OS.";

    // The one project whose namespace is not its name: platform contracts ship to scripts as
    // IntoChat.OS.Integrations (962ffb603). Closed by design review.
    private static readonly Dictionary<string, string> RootNamespaces = new(StringComparer.Ordinal)
    {
        ["IntoChat.OS.Authority.Contracts"] = "IntoChat.OS.Integrations",
    };

    [Fact]
    public void EveryProjectHasOneSolutionEntryInItsPhysicalGroup()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = Repository.Solution.Descendants("Project").ToArray();
        var paths = entries.Select(entry => entry.Attribute("Path")!.Value.Replace('\\', '/')).ToArray();
        Assert.Equal(paths.Length, paths.Distinct(comparer).Count());
        var disk = Repository.SourceFiles(Repository.SourceRoot, "*.csproj").Select(Repository.Relative).Order().ToArray();
        Assert.True(disk.SequenceEqual(paths.Order(), comparer),
            "Solution entries differ from the projects on disk; fix with `dotnet run scripts/sync-solution.cs`.\nOn disk only: "
            + string.Join(", ", disk.Except(paths, comparer)) + "\nIn the solution only: " + string.Join(", ", paths.Except(disk, comparer)));
        var sourceRoot = Repository.Relative(Repository.SourceRoot);
        var modulesRoot = Repository.Relative(Repository.ModulesRoot);
        foreach (var entry in entries)
        {
            var path = entry.Attribute("Path")!.Value.Replace('\\', '/');
            var directory = Path.GetDirectoryName(path)!.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(path);
            Assert.Equal(name, Path.GetFileName(directory));
            var group = directory[..directory.LastIndexOf('/')];
            var folder = entry.Parent!.Name.LocalName == "Folder" ? entry.Parent.Attribute("Name")!.Value : null;
            var expected = ExpectedFolders(group, sourceRoot);
            Assert.True(expected.Contains(folder, StringComparer.Ordinal),
                $"{path} sits in solution folder '{folder}', not where its directory puts it ({string.Join(" or ", expected.Select(f => f ?? "<root>"))}).");
            var isTest = name.EndsWith(".Tests", StringComparison.Ordinal) || name.Contains(".Tests.", StringComparison.Ordinal);
            if (isTest && directory.StartsWith(modulesRoot + "/", StringComparison.Ordinal))
            {
                Assert.True(Path.GetFileName(group) == "tests", $"Module test project {path} belongs under its module's tests/ directory.");
            }
            var project = XDocument.Load(Path.Combine(Repository.Root, path));
            foreach (var reference in project.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value;
                if (include.Contains('@', StringComparison.Ordinal)) { continue; }
                // The OS references its modules by glob; the catalog facts check that closure.
                if (include.Contains('*', StringComparison.Ordinal)) { continue; }
                Assert.True(File.Exists(Path.Combine(Repository.Root, directory, include.Replace('\\', '/'))), $"Broken project reference: {path} -> {include}");
            }
            foreach (var property in project.Descendants().Where(element => element.Name.LocalName is "RootNamespace" or "AssemblyName" or "PackageId"))
            {
                var expectedValue = property.Name.LocalName == "RootNamespace" ? RootNamespaces.GetValueOrDefault(name, name) : name;
                Assert.Equal(expectedValue, property.Value);
            }
        }
    }

    // null stands for "no folder": the project is a direct child of the solution.
    private static string?[] ExpectedFolders(string group, string sourceRoot)
    {
        if (group == sourceRoot) { return [null]; }
        var segments = group[(sourceRoot.Length + 1)..].Split('/')
            .Select(segment => segment.StartsWith(Prefix, StringComparison.Ordinal) ? segment[Prefix.Length..] : segment)
            .ToArray();
        var folder = "/" + string.Join('/', segments) + "/";
        // A module's tests/ directory is grouped either with the module or as its own subfolder.
        return segments[^1] == "tests" ? [folder, "/" + string.Join('/', segments[..^1]) + "/"] : [folder];
    }
}
