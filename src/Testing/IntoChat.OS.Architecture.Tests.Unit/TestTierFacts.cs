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

    [Fact]
    public void IntegrationTierHostsThroughTestingSilo()
    {
        var offenders = new List<string>();
        foreach (var project in TestProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (!name.EndsWith(".Tests.Integration", StringComparison.Ordinal)) { continue; }
            var allRefs = References(project).Concat(Repository.PackageReferences(project)).ToArray();
            if (!allRefs.Any(r => r is "DigitalBrain.Testing.Silo" or "DigitalBrain.Testing"))
            {
                offenders.Add($"{name} -> missing DigitalBrain.Testing.Silo or DigitalBrain.Testing");
            }
            var aspire = allRefs.Where(r => r.StartsWith("IntoChat.OS.Aspire.", StringComparison.Ordinal)).ToArray();
            if (aspire.Length > 0)
            {
                offenders.Add($"{name} -> {string.Join(", ", aspire)}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Integration tier projects host on DigitalBrain.Testing.Silo or DigitalBrain.Testing and never reference IntoChat.OS.Aspire.*:\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void EndToEndTierNeedsNoDocker()
    {
        string[] tokens = ["Docker" + "file", "docker ", "AddDocker" + "file"];
        var offenders = new List<string>();
        foreach (var project in Repository.TestProjects())
        {
            if (!project.Name.EndsWith(".Tests.E2E", StringComparison.Ordinal)) { continue; }
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs"))
            {
                var source = File.ReadAllText(file);
                if (tokens.Any(t => source.Contains(t, StringComparison.Ordinal)))
                {
                    offenders.Add(Repository.ProjectKey(file));
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "End-to-end tier projects run without Docker:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void UnitTierReferencesNoRuntime()
    {
        string[] forbidden = ["DigitalBrain.Runtime.Orleans", "DigitalBrain.Runtime.Apps", "DigitalBrain.Testing.Silo"];
        var offenders = new List<string>();
        foreach (var project in TestProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (!name.EndsWith(".Tests.Unit", StringComparison.Ordinal)) { continue; }
            var allRefs = References(project).Concat(Repository.PackageReferences(project)).ToArray();
            var bad = allRefs.Where(r => forbidden.Contains(r, StringComparer.Ordinal)).ToArray();
            if (bad.Length > 0)
            {
                offenders.Add($"{name} -> {string.Join(", ", bad)}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Unit tier projects reference no runtime or silo package:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoTestProjectIsEmpty()
    {
        string[] attributes = ["[Fact", "[Theory", "[Scenario"];
        var offenders = new List<string>();
        foreach (var project in Repository.TestProjects())
        {
            if (project.Name.EndsWith(".Tests.Support", StringComparison.Ordinal)) { continue; }
            var hasTests = Repository.SourceFiles(project.Directory, "*.cs")
                .Any(file =>
                {
                    var text = File.ReadAllText(file);
                    return attributes.Any(attr => text.Contains(attr, StringComparison.Ordinal));
                });
            if (!hasTests)
            {
                offenders.Add(project.Name);
            }
        }
        Assert.True(offenders.Count == 0,
            "Every test project has at least one [Fact], [Theory] or [Scenario]:\n" + string.Join('\n', offenders));
    }

    private static IEnumerable<string> TestProjects() => Repository.TestProjects().Select(project => project.Path);

    private static IEnumerable<string> ModuleTestProjects()
        => TestProjects().Where(path => path.StartsWith(Repository.ModulesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static IEnumerable<string> References(string project) => Repository.References(project);
}
