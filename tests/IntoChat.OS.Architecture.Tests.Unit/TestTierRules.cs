using System.Globalization;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The tier contract carried by a test project's suffix: .Tests.Unit starts no host and never
// waits on the clock, .Tests.Integration hosts through DigitalBrain.Testing.Silo when it has facts,
// and only the nightly tiers (.Tests.Browser, .Tests.E2E) drive a browser. Every rule is absolute.
public sealed partial class TestTierRules
{
    internal static List<string> UnitTierHosts()
    {
        // Concatenated so this file never matches its own rule.
        string[] hosts =
        [
            "Tcp" + "Listener",
            "WebApplication" + ".Run",
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
        var offenders = UnitTierHosts();
        Assert.True(offenders.Count == 0,
            "A .Tests.Unit fact starts no host, opens no socket and never sleeps; move it to .Tests.Integration: "
            + string.Join(", ", offenders));
    }

    internal static List<string> IntegrationWithoutSilo()
    {
        var found = new List<string>();
        foreach (var project in Repository.TestProjects().Where(project => project.Name.EndsWith(".Tests.Integration", StringComparison.Ordinal)))
        {
            var facts = Repository.SourceFiles(project.Directory, "*.cs").Any(file => File.ReadAllText(file).Contains("[Fact", StringComparison.Ordinal));
            if (!facts) { continue; }
            if (!Repository.References(project.Path).Concat(Repository.PackageReferences(project.Path)).Contains("DigitalBrain.Testing.Silo", StringComparer.Ordinal)) { found.Add(project.Name); }
        }
        return found;
    }

    [Fact]
    public void IntegrationTierHostsOnTheSilo()
    {
        var offenders = IntegrationWithoutSilo();
        Assert.True(offenders.Count == 0,
            "A .Tests.Integration project with facts hosts through DigitalBrain.Testing.Silo: " + string.Join(", ", offenders));
    }

    internal static List<string> PlaywrightReferencers()
    {
        string[] owners =
        [
            "IntoChat.OS.Modules.Plugins",
            "IntoChat.OS.Modules.AI",
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
        var offenders = PlaywrightReferencers();
        Assert.True(offenders.Count == 0,
            "Microsoft.Playwright belongs to the nightly .Tests.Browser and .Tests.E2E tiers and their harnesses: "
            + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"Task\.Delay\(\s*(\d+)\s*\)")]
    private static partial Regex LiteralDelay();
}
