using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// MEAI's speech and realtime interfaces carry the MEAI001 experimental diagnostic. Only the AI
// and Devices modules may suppress it so no experimental MEAI type escapes into Host, Kernel,
// Apps, or the DigitalBrain framework.
public sealed partial class Meai001Facts
{
    private static readonly HashSet<string> AllowedProjects = new(StringComparer.Ordinal)
    {
        "IntoChat.OS.Modules.AI",
        "IntoChat.OS.Modules.Devices",
        "IntoChat.OS.Modules.Devices.Contracts",
    };

    [GeneratedRegex(@"#\s*pragma\s+warning\s+disable\b[^\r\n]*\bMEAI001\b", RegexOptions.CultureInvariant)]
    private static partial Regex PragmaMeai001();

    [GeneratedRegex(@"\bMEAI001\b", RegexOptions.CultureInvariant)]
    private static partial Regex WordMeai001();

    [Fact]
    public void Meai001StaysInAiAndDevices()
    {
        var offenders = Offenders();
        Assert.True(
            offenders.Length == 0,
            "MEAI001 suppression is allowed only in IntoChat.OS.Modules.AI, IntoChat.OS.Modules.Devices, "
            + "and IntoChat.OS.Modules.Devices.Contracts:\n"
            + string.Join('\n', offenders));
    }

    internal static string[] Offenders()
    {
        var hits = new List<string>();
        var allProjects = EnumerateProductionProjects().ToArray();

        foreach (var (name, csprojPath, directory) in allProjects)
        {
            var doc = XDocument.Load(csprojPath);
            var hasNoWarnMeai001 = doc.Descendants("NoWarn")
                .Any(el => WordMeai001().IsMatch(el.Value));
            if (hasNoWarnMeai001 && !AllowedProjects.Contains(name))
            {
                hits.Add($"{Path.GetRelativePath(Repository.Root, csprojPath).Replace('\\', '/')} (<NoWarn> contains MEAI001)");
            }

            if (AllowedProjects.Contains(name))
            {
                continue;
            }

            foreach (var csFile in Repository.SourceFiles(directory, "*.cs"))
            {
                if (!OwnsProject(allProjects, name, csFile))
                {
                    continue;
                }

                if (PragmaMeai001().IsMatch(File.ReadAllText(csFile)))
                {
                    hits.Add($"{Path.GetRelativePath(Repository.Root, csFile).Replace('\\', '/')} (#pragma warning disable MEAI001)");
                }
            }
        }

        return [.. hits.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static IEnumerable<(string Name, string Path, string Directory)> EnumerateProductionProjects()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var project in Repository.Projects.Values)
        {
            if (IsTestProject(project.Name))
            {
                continue;
            }

            if (seen.Add(project.Path))
            {
                yield return (project.Name, project.Path, project.Directory);
            }
        }

        foreach (var csproj in Repository.SourceFiles(Repository.FrameworkRoot, "*.csproj"))
        {
            var full = Path.GetFullPath(csproj);
            var name = Path.GetFileNameWithoutExtension(full);
            if (IsTestProject(name))
            {
                continue;
            }

            if (seen.Add(full))
            {
                yield return (name, full, Path.GetDirectoryName(full)!);
            }
        }
    }

    private static bool IsTestProject(string name)
        => name.Contains(".Tests", StringComparison.Ordinal);

    private static bool OwnsProject(
        (string Name, string Path, string Directory)[] projects,
        string expectedName,
        string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var owner = projects
            .Where(p => full.StartsWith(p.Directory + Path.DirectorySeparatorChar, comparison))
            .MaxBy(p => p.Directory.Length);
        return string.Equals(owner.Name, expectedName, StringComparison.Ordinal);
    }
}
