using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests.Unit;

// Orleans becomes a runtime provider (clean-core W1, decision D1). Two ratchets hold today's reach while the
// provider takes it over; both are source scans because ArchUnitNET neither sees test code nor counts call sites.
// Baselines are keyed by project name, so a directory move never touches them; they only shrink.
public sealed partial class RuntimeBoundaryFacts
{
    [GeneratedRegex(@"\bGetGrain\s*[<(]|\bIGrainFactory\b|\bIClusterClient\b", RegexOptions.CultureInvariant)]
    private static partial Regex GrainFactoryUse();

    // R-A1: grain-factory use per file, tests included. A file may lose uses, never gain them.
    [Fact]
    public void GrainFactoryUseOnlyShrinks()
    {
        var baseline = Baseline<Dictionary<string, int>>("GrainFactoryOwners.json");
        var current = Repository.ProjectFiles("*.cs")
            .Select(path => (Key: Repository.ProjectKey(path), Count: GrainFactoryUse().Count(File.ReadAllText(path))))
            .Where(file => file.Count > 0)
            .ToDictionary(file => file.Key, file => file.Count, StringComparer.Ordinal);

        var grown = current.Where(file => file.Value > baseline.GetValueOrDefault(file.Key))
            .Select(file => $"{file.Key}: {baseline.GetValueOrDefault(file.Key)} -> {file.Value}").Order(StringComparer.Ordinal).ToArray();
        Assert.True(grown.Length == 0,
            "New grain-factory use; reach neurons through IDigitalBrain, or deliberately raise GrainFactoryOwners.json:\n" + string.Join('\n', grown));

        var shrunk = baseline.Where(file => current.GetValueOrDefault(file.Key) < file.Value)
            .Select(file => $"{file.Key}: {file.Value} -> {current.GetValueOrDefault(file.Key)}").Order(StringComparer.Ordinal).ToArray();
        Assert.True(shrunk.Length == 0,
            "Grain-factory use went down; shrink GrainFactoryOwners.json so the ratchet holds:\n" + string.Join('\n', shrunk));
    }

    // R-O1: direct Orleans package references per project. A project may drop packages, never add them; the list
    // shrinks to the Orleans provider, the hosts and the test harness.
    [Fact]
    public void OrleansPackageReferencesOnlyShrink()
    {
        var baseline = Baseline<Dictionary<string, string[]>>("OrleansReferenceOwners.json");
        var current = OrleansReferences();

        var added = current.SelectMany(project => project.Value
                .Except(baseline.GetValueOrDefault(project.Key) ?? [], StringComparer.Ordinal)
                .Select(package => $"{project.Key}: {package}"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0,
            "New Orleans package references; only the Orleans provider, hosts and the harness reference Orleans "
            + "(or deliberately extend OrleansReferenceOwners.json):\n" + string.Join('\n', added));

        var dropped = baseline.SelectMany(project => project.Value
                .Except(current.GetValueOrDefault(project.Key) ?? [], StringComparer.Ordinal)
                .Select(package => $"{project.Key}: {package}"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(dropped.Length == 0,
            "Orleans package references went away; shrink OrleansReferenceOwners.json so the ratchet holds:\n" + string.Join('\n', dropped));
    }

    // Every Orleans package pinned centrally is referenced by some project; an unused pin blurs the R-O1 picture.
    [Fact]
    public void EveryCentralOrleansPinIsReferenced()
    {
        var pinned = System.Xml.Linq.XDocument.Load(Path.Combine(Repository.Root, "Directory.Packages.props"))
            .Descendants("PackageVersion").Select(version => version.Attribute("Include")?.Value).OfType<string>()
            .Where(IsOrleansPackage).ToHashSet(StringComparer.Ordinal);
        var referenced = Repository.Projects.Values.SelectMany(project => Repository.PackageReferences(project.Path)).ToHashSet(StringComparer.Ordinal);

        var unused = pinned.Except(referenced).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unused.Length == 0, "Directory.Packages.props pins Orleans packages no project references:\n" + string.Join('\n', unused));
    }

    private static Dictionary<string, string[]> OrleansReferences()
        => Repository.Projects.Values
            .Select(project => (project.Name, Packages: Repository.PackageReferences(project.Path).Where(IsOrleansPackage).Distinct().Order(StringComparer.Ordinal).ToArray()))
            .Where(project => project.Packages.Length > 0)
            .ToDictionary(project => project.Name, project => project.Packages, StringComparer.Ordinal);

    private static bool IsOrleansPackage(string package)
        => package.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal) || package.Contains(".Orleans", StringComparison.Ordinal);

    private static T Baseline<T>(string file)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Repository.ProjectNamed("DigitalBrain.Architecture.Tests.Unit").Directory, file)))!;
}
