using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Workflow guard rails that no run can prove while Actions is unavailable:
// Only ci.yml, release.yml, and artcraft-images.yml exist in .github/workflows/.
// ci.yml runs unit, integration, e2e, and flutter jobs against a single build.
// release.yml builds images once and deploys test then prod via Kernel install with zero scripts.
// No workflow references scripts/ directory or PACKAGE_TOKEN or DEPLOYMENT_VALUES_TOKEN.
public sealed partial class CiWorkflowFacts
{
    private static string Workflow(string name) => File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", name));

    // The text of one top-level job: from "  <name>:" to the next two-space-indented key.
    private static string Job(string workflow, string name)
    {
        var match = Regex.Match(workflow, $@"^  {Regex.Escape(name)}:\s*$(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"job '{name}' not found");
        return match.Groups["body"].Value;
    }

    [Fact]
    public void OnlyCiReleaseAndArtcraftWarmWorkflowsExist()
    {
        var workflows = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml")
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        string[] expected = ["artcraft-images.yml", "ci.yml", "release.yml"];
        Assert.Equal(expected, workflows);
    }

    [Fact]
    public void CiRunsUnitIntegrationE2EAndFlutterJobsAgainstSingleBuild()
    {
        var ci = Workflow("ci.yml");

        // Single compilation in build job
        var build = Job(ci, "build");
        Assert.Contains("dotnet build Intochat.OS.slnx", build, StringComparison.Ordinal);
        Assert.Contains("gitleaks", build, StringComparison.Ordinal);
        Assert.Contains("upload-artifact", build, StringComparison.Ordinal);

        // Test jobs run with --no-build and download-artifact
        var testUnit = Job(ci, "test-unit");
        Assert.Contains("needs: [build]", testUnit, StringComparison.Ordinal);
        Assert.Contains("--no-build", testUnit, StringComparison.Ordinal);
        Assert.Contains("-p:IntoChatOSOnlyTier=Unit", testUnit, StringComparison.Ordinal);
        Assert.Contains("download-artifact", testUnit, StringComparison.Ordinal);

        var testIntegration = Job(ci, "test-integration");
        Assert.Contains("needs: [build]", testIntegration, StringComparison.Ordinal);
        Assert.Contains("--no-build", testIntegration, StringComparison.Ordinal);
        Assert.Contains("-p:IntoChatOSOnlyTier=Integration", testIntegration, StringComparison.Ordinal);
        Assert.Contains("azurite", testIntegration, StringComparison.Ordinal);
        Assert.Contains("postgres:16", testIntegration, StringComparison.Ordinal);

        var e2e = Job(ci, "e2e");
        Assert.Contains("needs: [build]", e2e, StringComparison.Ordinal);
        Assert.Contains("--no-build", e2e, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Tests.E2E.csproj", e2e, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter-trait \"needs=artcraft\"", e2e, StringComparison.Ordinal);

        var flutter = Job(ci, "flutter");
        Assert.Contains("flutter test", flutter, StringComparison.Ordinal);

        // Gating ci job needs all parallel jobs
        var gatingCi = Job(ci, "ci");
        var needsMatch = Regex.Match(gatingCi, @"(?m)^\s*needs:\s*\[(?<list>[^\]]*)\]");
        Assert.True(needsMatch.Success, "ci job must list its needs inline");
        var needs = needsMatch.Groups["list"].Value.Split(',').Select(n => n.Trim()).ToArray();
        Assert.Contains("build", needs);
        Assert.Contains("test-unit", needs);
        Assert.Contains("test-integration", needs);
        Assert.Contains("e2e", needs);
        Assert.Contains("flutter", needs);
    }

    [Fact]
    public void ReleaseBuildsImagesOnceAndDeploysTestThenProdViaKernelInstallWithZeroScripts()
    {
        var release = Workflow("release.yml");

        // Gating check-run verification
        var package = Job(release, "package");
        Assert.Contains("check-runs", package, StringComparison.Ordinal);

        // Builds images once with BuildKit GHA cache
        Assert.Contains("type=gha", package, StringComparison.Ordinal);
        Assert.Contains("acrintochattest.azurecr.io/os:", package, StringComparison.Ordinal);
        Assert.DoesNotContain("acrintochattest.azurecr.io/os-ui:", package, StringComparison.Ordinal);
        Assert.DoesNotContain("acrintochattest.azurecr.io/os-sandbox:", package, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", package, StringComparison.Ordinal);

        // deploy-test uses pre-built Kernel binary install
        var deployTest = Job(release, "deploy-test");
        Assert.Contains("IntoChat.OS.Kernel install --env test --config deploy/test.json --manifest release-manifest.json", deployTest, StringComparison.Ordinal);

        // deploy-prod uses environment: production and promote-from
        var deployProd = Job(release, "deploy-prod");
        Assert.Contains("environment: production", deployProd, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Kernel install --env prod --config deploy/prod.json --manifest release-manifest.json --promote-from", deployProd, StringComparison.Ordinal);

        // rollback_tag support
        Assert.Contains("rollback_tag", release, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWorkflowReferencesScriptsDirectoryOrPackageTokenOrDeploymentValuesToken()
    {
        var workflows = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml");
        foreach (var file in workflows)
        {
            var content = File.ReadAllText(file);
            var relativeName = Repository.Relative(file);

            Assert.False(content.Contains("scripts/", StringComparison.OrdinalIgnoreCase),
                $"Workflow {relativeName} references scripts/ directory.");
            Assert.False(content.Contains("PACKAGE_TOKEN", StringComparison.Ordinal),
                $"Workflow {relativeName} references PACKAGE_TOKEN.");
            Assert.False(content.Contains("DEPLOYMENT_VALUES_TOKEN", StringComparison.Ordinal),
                $"Workflow {relativeName} references DEPLOYMENT_VALUES_TOKEN.");
        }
    }

    [Fact]
    public void OnlyTheWarmWorkflowMayWritePackages()
    {
        Assert.Contains("packages: write", Workflow("artcraft-images.yml"), StringComparison.Ordinal);
        // release.yml packs and publishes DigitalBrain.* to GitHub Packages using secrets.GITHUB_TOKEN.
        var release = Workflow("release.yml");
        Assert.Contains("packages: write", release, StringComparison.Ordinal);
        var ci = Workflow("ci.yml");
        Assert.DoesNotContain("packages: write", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarmWorkflowUsesOnlyTheRepositoryToken()
    {
        var secrets = Regex.Matches(Workflow("artcraft-images.yml"), @"secrets\.([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).Distinct();
        Assert.All(secrets, secret => Assert.Equal("GITHUB_TOKEN", secret));
    }

    [Fact]
    public void RestoreUsesOnlyCommittedSources()
    {
        var workflows = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml");
        var offenders = workflows
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("../nuget", StringComparison.Ordinal)
                    || text.Contains(".nuget-local", StringComparison.Ordinal);
            })
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offenders.Length == 0, "Workflows must restore from committed sources only: " + string.Join(", ", offenders));
    }
}
