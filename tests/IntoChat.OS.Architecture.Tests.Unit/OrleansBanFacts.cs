using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Orleans stays in DigitalBrain.Runtime.Orleans and in the Microsoft Orleans module.
// The silo host and its clients are the process that runs that runtime; they are not modules.
// The module allow-list is exactly the assemblies the ledger still names. It shrinks to zero in 0.8.0.
// Identity and Apps still compile grain types (the authority grains, and the Apps creator worker).
// They are a second closed list, not extra names on the shrinking allow-list, and they leave in the same pass.
public sealed class OrleansBanFacts
{
    private static readonly string[] Allow =
    [
        "IntoChat.OS.Modules.AI",
        "IntoChat.OS.Modules.Shell",
    ];

    private static readonly string[] SiloHost =
    [
        "IntoChat.OS",
        "IntoChat.OS.Framework",
        "IntoChat.OS.Kernel",
    ];

    private static readonly string[] UnportedGrainModules =
    [
        "IntoChat.OS.Modules.Catalog",
        "IntoChat.OS.Apps",
    ];

    [Fact]
    public void TheModuleAllowListIsExactlyTheLedgerFive()
        => Assert.Equal(
            ["IntoChat.OS.Modules.AI", "IntoChat.OS.Modules.Shell"],
            Allow.Order(StringComparer.Ordinal).ToArray());

    [Fact]
    public void OrleansStaysInTheRuntimeTheOrleansModuleAndTheAllowList()
    {
        var offenders = new List<string>();
        foreach (var project in Repository.Projects.Values.Concat(FrameworkProjects()))
        {
            if (Permitted(project.Name, project.Path)) { continue; }
            foreach (var package in Repository.PackageReferences(project.Path).Where(package => package.StartsWith("Microsoft.Orleans", StringComparison.Ordinal)))
            {
                if (project.Name.EndsWith(".Contracts", StringComparison.Ordinal) && package == "Microsoft.Orleans.Sdk") { continue; }
                offenders.Add($"{project.Name}: PackageReference {package}");
            }
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs"))
            {
                if (!Owns(project, file)) { continue; }
                var text = File.ReadAllText(file);
                if (ReachesOrleans(text)) { offenders.Add($"{project.Name}: {Repository.Relative(file)}"); }
            }
        }
        Assert.True(offenders.Count == 0,
            "Orleans is banned outside the runtime, the Orleans module, the silo host, and the 0.8.0 allow-list:\n" + string.Join('\n', offenders.Order(StringComparer.Ordinal)));
    }

    // Grain reach: a package is not required when the framework already flows the attribute.
    // [GenerateSerializer] is persisted wire format and is not this ban.
    private static bool ReachesOrleans(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^\s*using\s+Orleans\b")
            || text.Contains("IGrainWithStringKey", StringComparison.Ordinal)
            || text.Contains("IGrainWithIntegerKey", StringComparison.Ordinal)
            || text.Contains("IGrainFactory", StringComparison.Ordinal);

    private static bool Permitted(string name, string path)
    {
        if (name.Contains(".Tests", StringComparison.Ordinal) || name.Contains(".Testing", StringComparison.Ordinal)) { return true; }
        var relative = path.Replace('\\', '/');
        if (relative.Contains("DigitalBrain.Runtime.Orleans", StringComparison.Ordinal)) { return true; }
        if (relative.Contains("Modules/Microsoft/Orleans", StringComparison.Ordinal) || relative.Contains("Modules/Orleans", StringComparison.Ordinal)) { return true; }
        if (relative.Contains("Modules/Microsoft/GitHub", StringComparison.Ordinal) || relative.Contains("Modules/GitHub", StringComparison.Ordinal)) { return true; }
        return Allow.Contains(name, StringComparer.Ordinal)
            || SiloHost.Contains(name, StringComparer.Ordinal)
            || UnportedGrainModules.Contains(name, StringComparer.Ordinal);
    }

    private static IEnumerable<Repository.Project> FrameworkProjects()
        => Repository.Solution.Descendants("Project").Where(Repository.IsFrameworkEntry).Select(entry =>
        {
            var relative = entry.Attribute("Path")!.Value.Replace('\\', '/');
            return new Repository.Project(Path.GetFileNameWithoutExtension(relative), Path.GetFullPath(Path.Combine(Repository.Root, relative)), "/framework/");
        });

    private static bool Owns(Repository.Project project, string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var projects = Repository.Projects.Values.Concat(FrameworkProjects());
        var owner = projects.Where(candidate => full.StartsWith(candidate.Directory + Path.DirectorySeparatorChar, comparison))
            .MaxBy(candidate => candidate.Directory.Length);
        return owner?.Name == project.Name;
    }
}
