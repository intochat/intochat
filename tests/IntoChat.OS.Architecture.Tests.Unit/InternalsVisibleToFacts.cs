using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// TEST-02 (D-08): an assembly shows its internals to its own tests only. A grant is allowed when the grantee is
// <grantor>.Tests or <grantor>.Tests.*, or when an <module>.Aspire.Hosting grantor grants <module>.Tests.Hosting.
// Every other grant, and every duplicate grant, fails: there is no baseline. A test that needs a sibling's internals
// moves next to its owner or uses the public surface.
// Source scan of csproj items and [assembly: InternalsVisibleTo] attributes, so test projects are covered too.
public sealed partial class InternalsVisibleToFacts
{
    [GeneratedRegex(@"\[\s*assembly\s*:\s*(?:System\.Runtime\.CompilerServices\.)?InternalsVisibleTo(?:Attribute)?\s*\(\s*""(?<grantee>[^"",]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyAttribute();

    internal static IEnumerable<(string Grantor, string Grantee)> Grants()
    {
        foreach (var project in Repository.Projects.Values)
        {
            foreach (var include in XDocument.Load(project.Path).Descendants("InternalsVisibleTo")
                .Select(item => item.Attribute("Include")?.Value).OfType<string>())
            {
                yield return (project.Name, Expand(include, project.Name));
            }
        }
        foreach (var file in Repository.ProjectFiles("*.cs"))
        {
            var grantor = Repository.ProjectKey(file).Split('/')[0];
            foreach (Match match in AssemblyAttribute().Matches(File.ReadAllText(file)))
            {
                yield return (grantor, match.Groups["grantee"].Value.Trim());
            }
        }
    }

    private static string Expand(string include, string project)
        => include.Replace("$(AssemblyName)", project, StringComparison.Ordinal).Replace("$(MSBuildProjectName)", project, StringComparison.Ordinal).Trim();

    internal static bool Allowed(string grantor, string grantee)
    {
        if (grantee == grantor + ".Tests" || grantee.StartsWith(grantor + ".Tests.", StringComparison.Ordinal))
        {
            return true;
        }
        // Consolidated test projects test all OS modules
        if (grantee is "IntoChat.OS.Tests.Unit" or "IntoChat.OS.Tests.Integration" or "IntoChat.OS.Tests.E2E"
            or "IntoChat.OS.Architecture.Tests.Unit" or "IntoChat.OS.Tests.Support")
        {
            return true;
        }
        const string hosting = ".Aspire.Hosting";
        return grantor.EndsWith(hosting, StringComparison.Ordinal) && grantee == grantor[..^hosting.Length] + ".Tests.Hosting";
    }

    internal static string[] Capture()
    {
        var grants = Grants().ToArray();
        var offenders = grants.Where(grant => !Allowed(grant.Grantor, grant.Grantee)).Select(grant => $"{grant.Grantor} -> {grant.Grantee}");
        var duplicates = grants.GroupBy(grant => grant).Where(group => group.Count() > 1).Select(group => $"{group.Key.Grantor} -> {group.Key.Grantee} (duplicate)");
        return offenders.Concat(duplicates).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    [Theory]
    [InlineData("IntoChat.OS.Framework", "IntoChat.OS.Framework.Tests.Integration", true)]
    [InlineData("IntoChat.OS.Framework", "IntoChat.OS.Framework.Tests", true)]
    [InlineData("IntoChat.OS.Modules.AI.Aspire.Hosting", "IntoChat.OS.Modules.AI.Tests.Hosting", true)]
    [InlineData("IntoChat.OS.Framework", "IntoChat.OS.Runtime.InMemory", false)]
    [InlineData("IntoChat.OS.Framework", "IntoChat.OS.Framework.TestsX", false)]
    [InlineData("IntoChat.OS.Modules.AI.Aspire.Hosting", "IntoChat.OS.Modules.Gmail.Tests.Hosting", false)]
    public void OnlyOwnTestsMayBeGranted(string grantor, string grantee, bool expected)
        => Assert.Equal(expected, Allowed(grantor, grantee));

    [Fact]
    public void NoAssemblyShowsItsInternalsBeyondItsOwnTests()
    {
        var offenders = Capture();
        Assert.True(offenders.Length == 0, "InternalsVisibleTo grants beyond an assembly's own tests:\n" + string.Join('\n', offenders));
    }
}
