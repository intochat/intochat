using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The test-tier architecture: a module proves its mechanism in one in-process test project on
// DigitalBrain.Testing; a full product stack boots in a shared fixture, never per fact.
// Every list is absolute: there is no legacy baseline.
public sealed partial class TestTierFacts
{
    [Fact]
    public void AModuleTestProjectCarriesATierSuffix()
    {
        string[] tiers = [".Tests.Unit", ".Tests.Integration", ".Tests.E2E", ".Tests.Browser", ".Tests.Hosting"];
        var offenders = ModuleTestProjects()
            .Select(Path.GetFileNameWithoutExtension)
            // <Module>.Tests.Support is the module's shared test fixtures library, not a test project: it has no tier.
            .Where(name => !name!.EndsWith(".Tests.Support", StringComparison.Ordinal))
            .Where(name => !tiers.Any(tier => name!.EndsWith(tier, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(offenders.Length == 0,
            "A module test project names its tier, <Module>.Tests.Unit|Integration|E2E|Browser|Hosting: " + string.Join(", ", offenders));
    }

    internal static List<(string Name, string[] Hosting)> AspireReferencingModuleTests()
    {
        var found = new List<(string, string[])>();
        foreach (var project in ModuleTestProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (name.EndsWith(".Tests.Hosting", StringComparison.Ordinal)) { continue; }
            var hosting = References(project)
                .Where(reference => reference is "IntoChat.OS.Aspire.Server" || reference.EndsWith(".Aspire.Hosting", StringComparison.Ordinal))
                .ToArray();
            if (hosting.Length > 0) { found.Add((name, hosting)); }
        }
        return found;
    }

    [Fact]
    public void ModuleTestsStayInProcess()
    {
        var offenders = new List<string>();
        foreach (var project in ModuleTestProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (name.EndsWith(".Tests.Hosting", StringComparison.Ordinal))
            {
                var booting = References(project)
                    .Where(reference => reference is "DigitalBrain.Testing" or "DigitalBrain.Testing.Silo")
                    .ToArray();
                if (booting.Length > 0) { offenders.Add($"{name} -> {string.Join(", ", booting)}"); }
            }
        }
        foreach (var (name, hosting) in AspireReferencingModuleTests())
        {
            offenders.Add($"{name} -> {string.Join(", ", hosting)}");
        }
        Assert.True(offenders.Count == 0,
            "Module tests host the brain in-process (DigitalBrain.Testing); Aspire hosting belongs to the product tier: "
            + string.Join("; ", offenders));
    }

    internal static List<string> EndToEndReferencers()
    {
        var found = new List<string>();
        foreach (var project in TestProjects())
        {
            if (!References(project).Contains("IntoChat.OS.Testing.Aspire", StringComparer.Ordinal)) { continue; }
            found.Add(Path.GetFileNameWithoutExtension(project));
        }
        return found;
    }

    [Fact]
    public void TheEndToEndTierBelongsToTheProduct()
    {
        var offenders = EndToEndReferencers();
        Assert.True(offenders.Count == 0,
            "A module test does not reference the deleted Aspire test harness: " + string.Join(", ", offenders));
    }

    internal static List<string> FullStackBootFacts()
    {
        // Concatenated so this file never matches its own rule.
        string[] boots =
        [
            "E2ETest" + ".For<",
            "ProductE2E" + ".Create(",
        ];
        var found = new List<string>();
        foreach (var file in Repository.ProjectFiles("*Facts.cs"))
        {
            var source = File.ReadAllText(file);
            // Building a composition or application model is not a boot; a boot starts it.
            if (!source.Contains(".StartAsync(", StringComparison.Ordinal)) { continue; }
            var builders = boots.Append("DistributedApplication" + "TestingBuilder");
            if (!builders.Any(token => source.Contains(token, StringComparison.Ordinal))) { continue; }
            found.Add(Repository.ProjectKey(file));
        }
        return found;
    }

    [Fact]
    public void FullProductStacksBootInFixturesNotInFacts()
    {
        var offenders = FullStackBootFacts();
        Assert.True(offenders.Count == 0,
            "A full product stack boots once per suite, in a shared fixture; a fact leases it instead of booting its own: "
            + string.Join(", ", offenders));
    }

    private static IEnumerable<string> TestProjects() => Repository.TestProjects().Select(project => project.Path);

    private static IEnumerable<string> ModuleTestProjects()
        => TestProjects().Where(path => path.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static IEnumerable<string> References(string project) => Repository.References(project);
}
