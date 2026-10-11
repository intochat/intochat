using System.Text.Json;
using System.Xml.Linq;

namespace DigitalBrain.Architecture.Tests;

// The harness is three packages a consumer installs (DigitalBrain.Testing, .Aspire, .Playwright);
// product helpers live in DigitalBrain.OS.Testing. A test reaches a brain through the harness and
// becomes a caller through Person, never by stamping a caller context itself.
public sealed class TestingPackageFacts
{
    [Fact]
    public void NoTestProjectReferencesTheOrleansTestingHost()
    {
        var offenders = Repository.TestProjects()
            .Where(project => Repository.PackageReferences(project.Path).Contains("Microsoft.Orleans.TestingHost", StringComparer.Ordinal))
            .Select(project => project.Name)
            .ToArray();
        Assert.True(offenders.Length == 0, "Tests boot a brain through DigitalBrain.Testing, not Orleans.TestingHost: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PackableTestingProjectsReferenceNoPlatformInternalsOsOrAppHost()
    {
        foreach (var name in new[] { "DigitalBrain.Testing", "DigitalBrain.Testing.Aspire", "DigitalBrain.Testing.Playwright" })
        {
            var forbidden = Repository.References(Repository.ProjectNamed(name).Path)
                .Where(reference => reference is "DigitalBrain.OS" or "DigitalBrain.AppHost"
                    || reference.StartsWith("DigitalBrain.Platform", StringComparison.Ordinal))
                .ToArray();
            Assert.True(forbidden.Length == 0, $"{name} ships to consumers and references {string.Join(", ", forbidden)}.");
        }
    }

    [Fact]
    public void NoTestOutsideTheHarnessStampsACallerByHand()
    {
        // Concatenated so this file never matches its own rule.
        var stamp = "CallerContextStamper" + ".Stamp(";
        var harness = Repository.ProjectNamed("DigitalBrain.Testing").Folder;
        var baseline = JsonSerializer.Deserialize<StampBaseline>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestTierBaseline.json")))!
            .LegacyHandStampedCallers.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var project in Repository.TestProjects().Where(project => project.Folder != harness))
        {
            if (!Repository.SourceFiles(project.Directory, "*.cs").Any(file => File.ReadAllText(file).Contains(stamp, StringComparison.Ordinal))) { continue; }
            if (!baseline.Remove(project.Name)) { offenders.Add(project.Name); }
        }
        Assert.True(offenders.Count == 0, "Use Person.AsCaller(): " + string.Join(", ", offenders));
        Assert.True(baseline.Count == 0,
            "Swept projects linger in TestTierBaseline.json LegacyHandStampedCallers; remove: " + string.Join(", ", baseline));
    }

    [Fact]
    public void OnlyTheOrleansModuleReferencesAzureStorageForOrleans()
    {
        // Silo hosts get Orleans' external systems from the orleans module. These are not OS silo hosts.
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DigitalBrain.Modules.Microsoft.Orleans"] = "the module that owns Orleans' Azure storage",
            ["DigitalBrain.Aspire.Client"] = "an Orleans client joining the cluster through Azure table clustering",
            ["DigitalBrain.Testing.Aspire"] = "the harness's Orleans client to an Aspire-booted brain",
            ["DigitalBrain.Trust"] = "the trust cluster's own silo, a separate host outside the OS",
        };
        var offenders = Repository.Projects.Values
            .Where(project => !allowed.ContainsKey(project.Name))
            .SelectMany(project => Repository.PackageReferences(project.Path)
                .Where(package => package.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal) && package.EndsWith(".AzureStorage", StringComparison.Ordinal))
                .Select(package => $"{project.Name} -> {package}"))
            .ToArray();
        Assert.True(offenders.Length == 0, "Orleans' Azure storage belongs to the orleans module: " + string.Join("; ", offenders));
    }

    private sealed record StampBaseline(string[] LegacyHandStampedCallers);
}
