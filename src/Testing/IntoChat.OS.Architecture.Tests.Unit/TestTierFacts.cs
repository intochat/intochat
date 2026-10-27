using System.Text.Json;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The test-tier architecture: a module proves its mechanism in one in-process test project on
// IntoChat.OS.Testing; the product proves the composition through the Aspire tier on
// IntoChat.OS.Testing.Aspire (product helpers in IntoChat.OS.Kernel.Testing); a full product stack boots in a shared fixture, never per fact.
// TestTierBaseline.json records the not-yet-migrated legacy state. Every list only shrinks:
// a new entry authorizes an architecture regression and needs the same deliberate review as
// the persisted-state baseline.
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
        var baseline = Baseline().LegacyAspireReferencingModuleTests.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var project in ModuleTestProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (name.EndsWith(".Tests.Hosting", StringComparison.Ordinal))
            {
                // The composition tier must stay model-only: no in-process brain, no product boot.
                var booting = References(project)
                    .Where(reference => reference is "IntoChat.OS.Testing" or "IntoChat.OS.Testing.Aspire" or "IntoChat.OS.Kernel.Testing")
                    .ToArray();
                if (booting.Length > 0) { offenders.Add($"{name} -> {string.Join(", ", booting)}"); }
                continue;
            }
        }
        foreach (var (name, hosting) in AspireReferencingModuleTests())
        {
            if (!baseline.Remove(name)) { offenders.Add($"{name} -> {string.Join(", ", hosting)}"); }
        }
        Assert.True(offenders.Count == 0,
            "Module tests host the brain in-process (IntoChat.OS.Testing); Aspire hosting belongs to the product tier: "
            + string.Join("; ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyAspireReferencingModuleTests; remove: " + string.Join(", ", baseline));
    }

    internal static List<string> EndToEndReferencers()
    {
        string[] productScope = ["IntoChat.OS.Framework.Tests.E2E", "IntoChat.OS.Aspire.Hosting.Tests.E2E", "IntoChat.OS.Kernel.Tests.E2E", "IntoChat.OS.Kernel.Tests.Integration", "IntoChat.OS.Kernel.Tests.Unit", "IntoChat.OS.Kernel.Testing"];
        var found = new List<string>();
        foreach (var project in TestProjects())
        {
            if (!References(project).Contains("IntoChat.OS.Testing.Aspire", StringComparer.Ordinal)) { continue; }
            var name = Path.GetFileNameWithoutExtension(project);
            // The harness's own folder (Testing) and the product tier may consume it.
            if (Repository.ProjectNamed(name).Folder == Repository.ProjectNamed("IntoChat.OS.Testing.Aspire").Folder
                || productScope.Contains(name, StringComparer.Ordinal))
            { continue; }
            found.Add(name);
        }
        return found;
    }

    [Fact]
    public void TheEndToEndTierBelongsToTheProduct()
    {
        var baseline = Baseline().LegacyEndToEndReferencers.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var name in EndToEndReferencers())
        {
            if (!baseline.Remove(name)) { offenders.Add(name); }
        }
        Assert.True(offenders.Count == 0,
            "IntoChat.OS.Testing.Aspire is the product's tier (IntoChat, Kernel, Platform, Aspire hosting). A module's edge belongs in its in-process .Tests project: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyEndToEndReferencers; remove: " + string.Join(", ", baseline));
    }

    internal static List<string> FullStackBootFacts()
    {
        // Concatenated so this file never matches its own rule.
        string[] boots =
        [
            "ReferenceBrain" + ".Create(",
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
        // Keyed "<ProjectName>/<path inside the project>"; a Windows checkout can carry different
        // directory casing than the git index.
        var baseline = Baseline().LegacyFullStackBootFacts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();
        foreach (var key in FullStackBootFacts())
        {
            if (!baseline.Remove(key)) { offenders.Add(key); }
        }
        Assert.True(offenders.Count == 0,
            "A full product stack boots once per suite, in a shared fixture; a fact leases it instead of booting its own: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated facts linger in TestTierBaseline.json LegacyFullStackBootFacts; remove: " + string.Join(", ", baseline));
    }

    private sealed record TestTierBaseline(
        string[] LegacyEndToEndReferencers,
        string[] LegacyAspireReferencingModuleTests,
        string[] LegacyFullStackBootFacts);

    private static TestTierBaseline Baseline()
        => JsonSerializer.Deserialize<TestTierBaseline>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestTierBaseline.json")))!;

    private static IEnumerable<string> TestProjects() => Repository.TestProjects().Select(project => project.Path);

    private static IEnumerable<string> ModuleTestProjects()
        => TestProjects().Where(path => path.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static IEnumerable<string> References(string project) => Repository.References(project);
}
