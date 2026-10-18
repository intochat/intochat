using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests;

// The tier contract carried by a test project's suffix: .Tests.Unit starts no host and never
// waits on the clock, .Tests.Integration shares one in-process host per assembly through an
// xunit assembly fixture, and only the nightly tiers (.Tests.Browser, .Tests.E2E) drive a
// browser. Every rule ratchets against TestTierBaseline.json: the lists only shrink.
public sealed partial class TestTierRules
{
    [Fact]
    public void UnitTierStartsNoHost()
    {
        // Concatenated so this file never matches its own rule.
        string[] hosts =
        [
            "TestBrain" + ".Create(",
            "RuntimeBrain" + ".",
            "Tcp" + "Listener",
            "WebApplication" + ".Create",
            "Thread" + ".Sleep(",
        ];
        var baseline = Baseline().LegacyUnitTierHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();
        var files = Repository.ProjectFiles(
            "*.cs",
            path => path.EndsWith("Facts.cs", StringComparison.Ordinal) || path.EndsWith("Fixture.cs", StringComparison.Ordinal),
            project => project.Name.EndsWith(".Tests.Unit", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var hosting = hosts.Any(token => source.Contains(token, StringComparison.Ordinal))
                || LiteralDelay().Matches(source).Any(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= 100);
            if (!hosting) { continue; }
            var key = Repository.ProjectKey(file);
            if (!baseline.Remove(key)) { offenders.Add(key); }
        }
        Assert.True(offenders.Count == 0,
            "A .Tests.Unit fact starts no host, opens no socket and never sleeps; move it to .Tests.Integration: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated facts linger in TestTierBaseline.json LegacyUnitTierHosts; remove: " + string.Join(", ", baseline));
    }

    [Fact]
    public void IntegrationTierUsesTheAssemblyFixture()
    {
        var marker = "[assembly: " + "AssemblyFixture(typeof(";
        var baseline = Baseline().LegacyIntegrationWithoutFixture.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var project in Repository.TestProjects().Where(project => project.Name.EndsWith(".Tests.Integration", StringComparison.Ordinal)))
        {
            var fixtures = Repository.SourceFiles(project.Directory, "*.cs")
                .Sum(file => Regex.Count(File.ReadAllText(file), Regex.Escape(marker)));
            if (fixtures == 1) { continue; }
            if (!baseline.Remove(project.Name)) { offenders.Add($"{project.Name} ({fixtures})"); }
        }
        Assert.True(offenders.Count == 0,
            "A .Tests.Integration project boots one shared host through exactly one [assembly: AssemblyFixture(...)]: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyIntegrationWithoutFixture; remove: " + string.Join(", ", baseline));
    }

    [Fact]
    public void BrowserTierOwnsPlaywright()
    {
        string[] owners =
        [
            "DigitalBrain.Testing.Playwright",
            "DigitalBrain.OS.Testing",
            "DigitalBrain.Modules.Microsoft.Playwright",
            "DigitalBrain.Modules.AI",
            "DigitalBrain.Modules.Flutter",
        ];
        var baseline = Baseline().LegacyPlaywrightReferencers.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var project in Repository.Projects.Values)
        {
            if (project.Name.EndsWith(".Tests.Browser", StringComparison.Ordinal)
                || project.Name.EndsWith(".Tests.E2E", StringComparison.Ordinal)
                || owners.Contains(project.Name, StringComparer.Ordinal))
            { continue; }
            // Direct package references only; a transitive Playwright arrives through an owner.
            if (!Repository.PackageReferences(project.Path).Any(package => package == "Microsoft.Playwright"
                || package.StartsWith("Microsoft.Playwright.", StringComparison.Ordinal)))
            { continue; }
            if (!baseline.Remove(project.Name)) { offenders.Add(project.Name); }
        }
        Assert.True(offenders.Count == 0,
            "Microsoft.Playwright belongs to the nightly .Tests.Browser and .Tests.E2E tiers and their harnesses: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyPlaywrightReferencers; remove: " + string.Join(", ", baseline));
    }

    [GeneratedRegex(@"Task\.Delay\(\s*(\d+)\s*\)")]
    private static partial Regex LiteralDelay();

    private sealed record TestTierBaseline(
        string[] LegacyUnitTierHosts,
        string[] LegacyIntegrationWithoutFixture,
        string[] LegacyPlaywrightReferencers);

    private static TestTierBaseline Baseline()
        => JsonSerializer.Deserialize<TestTierBaseline>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestTierBaseline.json")))!;
}
