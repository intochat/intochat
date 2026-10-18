using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Module options bind through one mechanism (Module<TOptions> and GetModuleOptions). Ad hoc configuration reads in
// module source are how the five legacy binding styles arose. Every remaining read is listed with a reason; the list
// only shrinks. Entries are keyed by "<ProjectName>/<path inside the project>" plus the pattern name, not line numbers.
public sealed partial class ModuleConfigurationReadFacts
{
    private sealed record Owner(string File, string Pattern, string Reason);

    private static readonly (string Name, Regex Expression)[] Patterns =
    [
        ("GetSection", GetSection()),
        ("Bind", Bind()),
        ("GetOfT", GetOfT()),
        ("Indexer", Indexer()),
        ("BindConfiguration", BindConfiguration()),
    ];

    [GeneratedRegex(@"\.GetSection\(", RegexOptions.CultureInvariant)]
    private static partial Regex GetSection();

    [GeneratedRegex(@"\.Bind\(", RegexOptions.CultureInvariant)]
    private static partial Regex Bind();

    [GeneratedRegex(@"\.Get<[A-Za-z]+>\(\)", RegexOptions.CultureInvariant)]
    private static partial Regex GetOfT();

    [GeneratedRegex(@"[Cc]onfiguration\[", RegexOptions.CultureInvariant)]
    private static partial Regex Indexer();

    [GeneratedRegex(@"AddOptions<[^>]+>\(\)\s*\.BindConfiguration", RegexOptions.CultureInvariant)]
    private static partial Regex BindConfiguration();

    private static IEnumerable<(string File, string Pattern)> Hits()
    {
        var modules = Repository.ModulesRoot + Path.DirectorySeparatorChar;
        return Repository.ProjectFiles("*.cs", path => !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
                project => project.Directory.StartsWith(modules, StringComparison.OrdinalIgnoreCase)
                    && !project.Name.EndsWith(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Tests.", StringComparison.Ordinal))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                var key = Repository.ProjectKey(path);
                return Patterns.Where(pattern => pattern.Expression.IsMatch(text)).Select(pattern => (key, pattern.Name));
            })
            .Distinct();
    }

    [Fact]
    public void LegacyBindingStylesOnlyInOwnerList()
    {
        var path = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "ModuleConfigurationReadOwners.json");
        var baseline = JsonSerializer.Deserialize<Owner[]>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Select(owner => (owner.File, owner.Pattern)).ToHashSet();
        var current = Hits().ToHashSet();

        var intruders = current.Except(baseline).Order().Select(hit => $"{hit.File} [{hit.Pattern}]").ToArray();
        Assert.True(intruders.Length == 0,
            "Module configuration read outside the owner list. Bind through Module<TOptions> / GetModuleOptions, or "
            + "deliberately add the file and pattern with a reason to ModuleConfigurationReadOwners.json:\n" + string.Join('\n', intruders));

        var stale = baseline.Except(current).Order().Select(hit => $"{hit.File} [{hit.Pattern}]").ToArray();
        Assert.True(stale.Length == 0,
            "These owner entries no longer match any read; shrink ModuleConfigurationReadOwners.json so the ratchet holds:\n" + string.Join('\n', stale));
    }
}
