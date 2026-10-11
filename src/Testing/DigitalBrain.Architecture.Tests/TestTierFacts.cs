using System.Text.Json;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Architecture.Tests;

// The test-tier architecture: a module proves its mechanism in one in-process test project on
// DigitalBrain.Testing; the product proves the composition through the Aspire tier on
// DigitalBrain.Testing.Aspire (product helpers in DigitalBrain.OS.Testing); a full product stack boots in a shared fixture, never per fact.
// TestTierBaseline.json records the not-yet-migrated legacy state. Every list only shrinks:
// a new entry authorizes an architecture regression and needs the same deliberate review as
// the persisted-state baseline.
public sealed class TestTierFacts
{
    [Fact]
    public void AModuleHasOneTestProjectNamedTests()
    {
        var baseline = Baseline().LegacyModuleTestProjects.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        var conformingPerDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in ModuleTestProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (name.Contains(".Tests.", StringComparison.Ordinal))
            {
                // <Module>.Tests.Hosting is the module's Aspire composition tier: it builds the
                // application model and reads annotations, never a running brain.
                if (name.EndsWith(".Tests.Hosting", StringComparison.Ordinal)) { continue; }
                if (!baseline.Remove(name)) { offenders.Add(name); }
                continue;
            }
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(project))!;
            conformingPerDirectory[directory] = conformingPerDirectory.GetValueOrDefault(directory) + 1;
        }
        Assert.True(offenders.Count == 0,
            "A module ships one test project named <Module>.Tests. Migrate instead of adding: " + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyModuleTestProjects; remove: " + string.Join(", ", baseline));
        var crowded = conformingPerDirectory.Where(entry => entry.Value > 1).Select(entry => entry.Key).ToArray();
        Assert.True(crowded.Length == 0, "One test project per module tests folder: " + string.Join(", ", crowded));
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
                    .Where(reference => reference is "DigitalBrain.Testing" or "DigitalBrain.Testing.Module" or "DigitalBrain.Testing.Aspire" or "DigitalBrain.OS.Testing")
                    .ToArray();
                if (booting.Length > 0) { offenders.Add($"{name} -> {string.Join(", ", booting)}"); }
                continue;
            }
            var hosting = References(project)
                .Where(reference => reference is "DigitalBrain.Aspire.Server" || reference.EndsWith(".Aspire.Hosting", StringComparison.Ordinal))
                .ToArray();
            if (hosting.Length == 0) { continue; }
            if (!baseline.Remove(name)) { offenders.Add($"{name} -> {string.Join(", ", hosting)}"); }
        }
        Assert.True(offenders.Count == 0,
            "Module tests host the brain in-process (DigitalBrain.Testing); Aspire hosting belongs to the product tier: "
            + string.Join("; ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyAspireReferencingModuleTests; remove: " + string.Join(", ", baseline));
    }

    [Fact]
    public void TheEndToEndTierBelongsToTheProduct()
    {
        string[] productScope = ["DigitalBrain.Framework.Tests.E2E", "DigitalBrain.Platform.Tests.E2E", "DigitalBrain.Aspire.Hosting.Tests.E2E", "DigitalBrain.OS.Tests.E2E", "DigitalBrain.OS.Tests.Unit", "DigitalBrain.OS.Testing"];
        var baseline = Baseline().LegacyEndToEndReferencers.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var project in TestProjects())
        {
            if (!References(project).Contains("DigitalBrain.Testing.Aspire", StringComparer.Ordinal)) { continue; }
            var name = Path.GetFileNameWithoutExtension(project);
            // The harness's own folder (Testing) and the product tier may consume it.
            if (Repository.ProjectNamed(name).Folder == Repository.ProjectNamed("DigitalBrain.Testing.Aspire").Folder
                || productScope.Contains(name, StringComparer.Ordinal))
            { continue; }
            if (!baseline.Remove(name)) { offenders.Add(name); }
        }
        Assert.True(offenders.Count == 0,
            "DigitalBrain.Testing.Aspire is the product's tier (IntoChat, Kernel, Platform, Aspire hosting). A module's edge belongs in its in-process .Tests project: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyEndToEndReferencers; remove: " + string.Join(", ", baseline));
    }

    [Fact]
    public void FullProductStacksBootInFixturesNotInFacts()
    {
        // Concatenated so this file never matches its own rule.
        string[] boots =
        [
            "ReferenceBrain" + ".Create(",
            "E2ETest" + ".For<",
            "ProductE2E" + ".Create(",
        ];
        // Keyed "<ProjectName>/<path inside the project>"; a Windows checkout can carry different
        // directory casing than the git index.
        var baseline = Baseline().LegacyFullStackBootFacts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();
        foreach (var file in Repository.ProjectFiles("*Facts.cs"))
        {
            var source = File.ReadAllText(file);
            // Building a composition or application model is not a boot; a boot starts it.
            if (!source.Contains(".StartAsync(", StringComparison.Ordinal)) { continue; }
            var builders = boots.Append("DistributedApplication" + "TestingBuilder");
            if (!builders.Any(token => source.Contains(token, StringComparison.Ordinal))) { continue; }
            var key = Repository.ProjectKey(file);
            if (!baseline.Remove(key)) { offenders.Add(key); }
        }
        Assert.True(offenders.Count == 0,
            "A full product stack boots once per suite, in a shared fixture; a fact leases it instead of booting its own: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated facts linger in TestTierBaseline.json LegacyFullStackBootFacts; remove: " + string.Join(", ", baseline));
    }

    private sealed record TestTierBaseline(
        string[] LegacyModuleTestProjects,
        string[] LegacyEndToEndReferencers,
        string[] LegacyAspireReferencingModuleTests,
        string[] LegacyFullStackBootFacts,
        string[] LegacyHandStampedCallers);

    private static TestTierBaseline Baseline()
        => JsonSerializer.Deserialize<TestTierBaseline>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestTierBaseline.json")))!;

    private static IEnumerable<string> TestProjects() => Repository.TestProjects().Select(project => project.Path);

    private static IEnumerable<string> ModuleTestProjects()
        => TestProjects().Where(path => path.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static IEnumerable<string> References(string project) => Repository.References(project);
}
