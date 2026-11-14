using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// os packs only IntoChat.OS* and IntoChat.Cloud.* packages (DIST-04). DigitalBrain* belongs to the digitalbrain
// repository and the deleted IntoChat.OS.Sdk* tier never comes back under its old name.
public sealed partial class PackageRootAllowlistFacts
{
    // The packable count after consolidating the 14 module Aspire.Hosting projects and removing unused stub modules (D3); a drop below it means the scan stopped seeing projects.
    private const int KnownPackable = 23;

    [GeneratedRegex(@"^IntoChat\.OS(\.|$)|^IntoChat\.Cloud(\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedRoot();

    internal static IReadOnlyList<(string Project, string PackageId)> PackableProjects()
        => Repository.Projects.Values
            .Select(project => (project, document: XDocument.Load(project.Path)))
            .Where(entry =>
            {
                var explicitSetting = entry.document.Descendants("IsPackable").LastOrDefault()?.Value.Trim();
                if (explicitSetting is not null)
                {
                    return explicitSetting == "true";
                }
                return entry.project.Directory.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            })
            .Select(entry => (entry.project.Name, entry.document.Descendants("PackageId").LastOrDefault()?.Value.Trim() ?? entry.project.Name))
            .ToArray();

    [Fact]
    public void EveryPackableProjectHasAnAllowedRoot()
    {
        var packable = PackableProjects();
        Assert.True(packable.Count >= KnownPackable, $"found {packable.Count} packable projects, expected at least {KnownPackable}");
        var offenders = packable
            .Where(entry => !AllowedRoot().IsMatch(entry.PackageId)
                || entry.PackageId.StartsWith("IntoChat.OS." + "Sdk", StringComparison.Ordinal)
                || entry.PackageId.StartsWith("DigitalBrain", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Project + " -> " + entry.PackageId).ToArray();
        Assert.True(offenders.Length == 0, "os packs IntoChat.OS* and IntoChat.Cloud.* only:\n" + string.Join('\n', offenders));
    }
}
