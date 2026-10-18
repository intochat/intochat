using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The harness is three packages a consumer installs (IntoChat.OS.Testing, .Aspire, .Playwright);
// product helpers live in IntoChat.OS.Kernel.Testing. A test reaches a brain through the harness and
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
        Assert.True(offenders.Length == 0, "Tests boot a brain through IntoChat.OS.Testing, not Orleans.TestingHost: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PackableTestingProjectsReferenceNoPlatformInternalsOsOrAppHost()
    {
        foreach (var name in new[] { "IntoChat.OS.Testing", "IntoChat.OS.Testing.Aspire", "IntoChat.OS.Testing.Playwright" })
        {
            var forbidden = Repository.References(Repository.ProjectNamed(name).Path)
                .Where(reference => reference is "IntoChat.OS.Kernel" or "IntoChat.OS.AppHost"
                    || reference.StartsWith("IntoChat.OS.Authority", StringComparison.Ordinal))
                .ToArray();
            Assert.True(forbidden.Length == 0, $"{name} ships to consumers and references {string.Join(", ", forbidden)}.");
        }
    }

    [Fact]
    public void NoTestOutsideTheHarnessStampsACallerByHand()
    {
        // Concatenated so this file never matches its own rule.
        var stamp = "CallerContextStamper" + ".Stamp(";
        // Facts about the stamp itself: the mechanism under test, not a way to become a caller.
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EnforcementFacts.cs"] = "the kernel's enforcement of a stamped caller, which a Person cannot forge",
        };
        var harness = Repository.ProjectNamed("IntoChat.OS.Testing").Folder;
        var stamping = Repository.TestProjects()
            .Where(project => project.Folder != harness)
            .SelectMany(project => Repository.SourceFiles(project.Directory, "*.cs")
                .Where(file => File.ReadAllText(file).Contains(stamp, StringComparison.Ordinal))
                .Select(file => (Project: project.Name, File: Path.GetFileName(file))))
            .ToArray();
        var offenders = stamping.Where(found => !allowed.ContainsKey(found.File)).Select(found => $"{found.Project}/{found.File}").ToArray();
        Assert.True(offenders.Length == 0, "Use Person.AsCaller(): " + string.Join(", ", offenders));
        var stale = allowed.Keys.Where(file => !stamping.Any(found => found.File == file)).ToArray();
        Assert.True(stale.Length == 0, "Allowed stamp files no longer stamp; remove: " + string.Join(", ", stale));
    }

    [Fact]
    public void OnlyTheOrleansModuleReferencesAzureStorageForOrleans()
    {
        // Silo hosts get Orleans' external systems from the orleans module. These are not OS silo hosts.
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IntoChat.OS.Modules.Microsoft.Orleans"] = "the module that owns Orleans' Azure storage",
            ["IntoChat.OS.Aspire.Client"] = "an Orleans client joining the cluster through Azure table clustering",
            ["IntoChat.OS.Testing.Aspire"] = "the harness's Orleans client to an Aspire-booted brain",
            ["IntoChat.OS.Trust"] = "the trust cluster's own silo, a separate host outside the OS",
        };
        var offenders = Repository.Projects.Values
            .Where(project => !allowed.ContainsKey(project.Name))
            .SelectMany(project => Repository.PackageReferences(project.Path)
                .Where(package => package.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal) && package.EndsWith(".AzureStorage", StringComparison.Ordinal))
                .Select(package => $"{project.Name} -> {package}"))
            .ToArray();
        Assert.True(offenders.Length == 0, "Orleans' Azure storage belongs to the orleans module: " + string.Join("; ", offenders));
    }
}
