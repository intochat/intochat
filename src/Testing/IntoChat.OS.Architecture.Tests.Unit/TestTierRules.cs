using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The tier contract carried by a test project's suffix: .Tests.Unit starts no host and never
// waits on the clock, .Tests.Integration shares one in-process host per assembly through an
// xunit assembly fixture, and only the nightly tiers (.Tests.Browser, .Tests.E2E) drive a
// browser. Every rule ratchets against TestTierBaseline.json: the lists only shrink.
public sealed partial class TestTierRules
{
    internal static List<string> UnitTierHosts()
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
        var found = new List<string>();
        var files = Repository.ProjectFiles(
            "*.cs",
            projects: project => project.Name.EndsWith(".Tests.Unit", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var hosting = hosts.Any(token => source.Contains(token, StringComparison.Ordinal))
                || LiteralDelay().Matches(source).Any(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= 100);
            if (hosting) { found.Add(Repository.ProjectKey(file)); }
        }
        return found;
    }

    [Fact]
    public void UnitTierStartsNoHost()
    {
        var baseline = Baseline().LegacyUnitTierHosts.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var key in UnitTierHosts())
        {
            if (!baseline.Remove(key)) { offenders.Add(key); }
        }
        Assert.True(offenders.Count == 0,
            "A .Tests.Unit fact starts no host, opens no socket and never sleeps; move it to .Tests.Integration: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated facts linger in TestTierBaseline.json LegacyUnitTierHosts; remove: " + string.Join(", ", baseline));
    }

    internal static List<(string Name, int Fixtures)> IntegrationWithoutFixture()
    {
        var marker = "[assembly: " + "AssemblyFixture(typeof(";
        var found = new List<(string, int)>();
        foreach (var project in Repository.TestProjects().Where(project => project.Name.EndsWith(".Tests.Integration", StringComparison.Ordinal)))
        {
            var fixtures = Repository.SourceFiles(project.Directory, "*.cs")
                .Sum(file => Regex.Count(File.ReadAllText(file), Regex.Escape(marker)));
            if (fixtures != 1) { found.Add((project.Name, fixtures)); }
        }
        return found;
    }

    [Fact]
    public void IntegrationTierUsesTheAssemblyFixture()
    {
        var baseline = Baseline().LegacyIntegrationWithoutFixture.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var (name, fixtures) in IntegrationWithoutFixture())
        {
            if (!baseline.Remove(name)) { offenders.Add($"{name} ({fixtures})"); }
        }
        Assert.True(offenders.Count == 0,
            "A .Tests.Integration project boots one shared host through exactly one [assembly: AssemblyFixture(...)]: "
            + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Migrated projects linger in TestTierBaseline.json LegacyIntegrationWithoutFixture; remove: " + string.Join(", ", baseline));
    }

    internal static List<string> PlaywrightReferencers()
    {
        string[] owners =
        [
            "IntoChat.OS.Testing.Playwright",
            "IntoChat.OS.Kernel.Testing",
            "IntoChat.OS.Modules.Microsoft.Playwright",
            "IntoChat.OS.Modules.AI",
            "IntoChat.OS.Modules.Flutter",
        ];
        var found = new List<string>();
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
            found.Add(project.Name);
        }
        return found;
    }

    [Fact]
    public void BrowserTierOwnsPlaywright()
    {
        var baseline = Baseline().LegacyPlaywrightReferencers.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var name in PlaywrightReferencers())
        {
            if (!baseline.Remove(name)) { offenders.Add(name); }
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
