using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Module options bind through one mechanism (Module<TOptions> and GetModuleOptions). Ad hoc configuration reads in
// module source are how the five legacy binding styles arose. Host keys go through HostConfiguration in the Framework.
// The owner list is closed at empty: the fact asserts it, so it cannot grow back without editing this fact.
// The scan covers module assemblies only; a Sandbox executable is a process with its own configuration root.
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

    // A configuration bind: the binder, a configuration object or a section. A neuron's own Bind(reference) is not one.
    [GeneratedRegex(@"ConfigurationBinder\.Bind\(|[Cc]onfiguration\s*\.Bind\(|GetSection\([^;]*\)\s*\.Bind\(", RegexOptions.CultureInvariant)]
    private static partial Regex Bind();

    // Get<T>() on a configuration or section; a server-feature collection's Get<T>() is not one.
    [GeneratedRegex(@"(?:[Cc]onfiguration|GetSection\([^;]*\))\s*\.Get<[A-Za-z]+>\(\)", RegexOptions.CultureInvariant)]
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
                    && !project.Name.EndsWith(".Sandbox", StringComparison.Ordinal)
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
    public void ModuleCodeReadsConfigurationOnlyThroughOptionsAndHostConfiguration()
    {
        var path = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "ModuleConfigurationReadOwners.json");
        var owners = JsonSerializer.Deserialize<Owner[]>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(owners);

        var intruders = Hits().Order().Select(hit => $"{hit.File} [{hit.Pattern}]").ToArray();
        Assert.True(intruders.Length == 0,
            "Module configuration read in module source. Bind through Module<TOptions> / GetModuleOptions, or read a host "
            + "key through HostConfiguration." + Environment.NewLine + string.Join(Environment.NewLine, intruders));
    }
}
