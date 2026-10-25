using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// TEST-02 (D-08): an assembly shows its internals to its own tests only. A grant is allowed when the grantee is
// <grantor>.Tests or <grantor>.Tests.*, or when an <module>.Aspire.Hosting grantor grants <module>.Tests.Hosting.
// Every other grant, and every duplicate grant, is pinned in InternalsVisibleToBaseline.json and only shrinks.
// Source scan of csproj items and [assembly: InternalsVisibleTo] attributes, so test projects are covered too.
public sealed partial class InternalsVisibleToFacts
{
    internal const string BaselineFile = "InternalsVisibleToBaseline.json";

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
        const string hosting = ".Aspire.Hosting";
        return grantor.EndsWith(hosting, StringComparison.Ordinal) && grantee == grantor[..^hosting.Length] + ".Tests.Hosting";
    }

    internal static string[] Capture()
    {
        var grants = Grants().ToArray();
        var offenders = grants.Where(grant => !Allowed(grant.Grantor, grant.Grantee)).Select(grant => $"{grant.Grantor} -> {grant.Grantee}");
        var duplicates = grants.GroupBy(grant => grant).Where(group => group.Count() > 1).Select(group => $"{group.Key.Grantor} -> {group.Key.Grantee} (duplicate)");
        return CaptureWriter.Ordered(offenders.Concat(duplicates));
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
    public void CrossAssemblyInternalsOnlyShrink()
    {
        var baseline = JsonSerializer.Deserialize<string[]>(File.ReadAllText(CaptureWriter.Path(BaselineFile)))!;
        var current = Capture();
        var grew = current.Except(baseline, StringComparer.Ordinal).ToArray();
        Assert.True(grew.Length == 0, "New InternalsVisibleTo grants beyond an assembly's own tests:\n" + string.Join('\n', grew));
        var gone = baseline.Except(current, StringComparer.Ordinal).ToArray();
        Assert.True(gone.Length == 0, "InternalsVisibleTo offenders went away; regenerate with scripts/capture-all.ps1:\n" + string.Join('\n', gone));
    }

    [Fact(Explicit = true)]
    public void CaptureInternalsVisibleToBaseline() => CaptureWriter.Write(BaselineFile, Capture());
}
