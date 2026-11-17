using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Workflow guard rails that no run can prove while Actions is unavailable:
// Only ci.yml, release.yml, and artcraft-images.yml exist in .github/workflows/.
// ci.yml runs unit, integration, e2e, and flutter jobs against a single build.
// release.yml builds images once and packages releases with no deploy scripts; deploy jobs live in the private cloud repository.
// No workflow references scripts/ directory or PACKAGE_TOKEN or DEPLOYMENT_VALUES_TOKEN.
// release.yml packages the Windows bundle with eng/release/package-release.ps1 on a Windows runner and attaches it to
// the release; ci.yml dry-runs the same script on Windows so it cannot rot between releases.
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

    // Azurite refuses a storage API version newer than its own release unless it is started with --skipApiVersionCheck.
    // A service container cannot pass that argument, so every job that uses Azurite starts it with docker run.
    [Theory]
    [InlineData("test-integration")]
    [InlineData("e2e")]
    public void EveryJobThatUsesAzuriteStartsItWithTheApiVersionCheckSkipped(string jobName)
    {
        var job = Job(Workflow("ci.yml"), jobName);

        Assert.Contains("AZURITE_CONNECTION_STRING", job, StringComparison.Ordinal);
        Assert.DoesNotContain("image: mcr.microsoft.com/azure-storage/azurite", job, StringComparison.Ordinal);
        var start = Regex.Match(job, @"docker run [^\r\n]*mcr\.microsoft\.com/azure-storage/azurite[^\r\n]*");
        Assert.True(start.Success, $"job '{jobName}' must start Azurite with docker run");
        Assert.Contains("--skipApiVersionCheck", start.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBuildsImagesOnceAndHasNoDeployJobs()
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
        Assert.Contains("IntoChat.OS.Kernel.csproj", package, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Kernel/Dockerfile", package, StringComparison.Ordinal);

        // Deploy jobs and deploy config live in the private cloud repository.
        Assert.DoesNotContain("deploy-test", release, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy-prod", release, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy/", release, StringComparison.Ordinal);

        // rollback_tag support
        Assert.Contains("rollback_tag", release, StringComparison.Ordinal);
    }

    private static string[] Needs(string job)
    {
        var match = Regex.Match(job, @"(?m)^\s*needs:\s*\[(?<list>[^\]]*)\]");
        return match.Success ? match.Groups["list"].Value.Split(',').Select(n => n.Trim()).ToArray() : [];
    }

    private static string[] Uses(string text) =>
        Regex.Matches(text, @"(?m)^\s*(?:-\s*)?uses:\s*(?<action>\S+)").Select(m => m.Groups["action"].Value).Distinct().ToArray();

    private const string PackagingScript = "eng/release/package-release.ps1";

    // The deploy facts above say "zero scripts": deploys go through Kernel install. Packaging the desktop bundle is the
    // one script a workflow runs, and it lives under eng/release, not the retired scripts/ directory.
    [Fact]
    public void ReleasePackagesTheWindowsBundleOnAWindowsRunnerAndAttachesItToTheRelease()
    {
        Assert.True(File.Exists(Path.Combine(Repository.Root, PackagingScript)), PackagingScript + " must exist");
        var release = Workflow("release.yml");
        var job = Job(release, "package-windows");

        Assert.Matches(@"(?m)^\s*runs-on:\s*windows-2025\s*$", job);
        Assert.Matches(@"(?m)^\s*timeout-minutes:\s*\d+\s*$", job);
        Assert.Contains("./" + PackagingScript + " -Rid win-x64", job, StringComparison.Ordinal);
        Assert.DoesNotContain("-SkipFlutter", job, StringComparison.Ordinal);
        Assert.DoesNotContain("-DryRun", job, StringComparison.Ordinal);

        // The same guards as the Linux package job: ci passed for this commit and the tag is the VERSION.
        Assert.Contains("check-runs", job, StringComparison.Ordinal);
        Assert.Contains("does not match VERSION", job, StringComparison.Ordinal);

        // .NET from global.json; Flutter pinned to the version ci tests the shell with.
        Assert.Contains("global-json-file: global.json", job, StringComparison.Ordinal);
        var flutterVersion = Regex.Match(Job(Workflow("ci.yml"), "flutter"), @"flutter-version:\s*'(?<v>[^']+)'");
        Assert.True(flutterVersion.Success, "ci flutter job must pin flutter-version");
        Assert.Contains($"flutter-version: '{flutterVersion.Groups["v"].Value}'", job, StringComparison.Ordinal);
        Assert.Contains("flutter pub get --enforce-lockfile", job, StringComparison.Ordinal);

        // The four kinds of asset go to the release the tag belongs to.
        var upload = Regex.Match(job, @"gh release upload [^
]*");
        Assert.True(upload.Success, "package-windows must attach its output with gh release upload");
        Assert.Contains("\"${GITHUB_REF_NAME}\"", upload.Value, StringComparison.Ordinal);
        Assert.Contains("intochat-*-win-x64.zip", upload.Value, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", upload.Value, StringComparison.Ordinal);
        Assert.Contains("latest.json", upload.Value, StringComparison.Ordinal);
        Assert.Contains("winget/IntoChat.OS.yaml", upload.Value, StringComparison.Ordinal);

        // Only the repository token, and only action versions another job already uses.
        Assert.DoesNotContain("secrets.", job, StringComparison.Ordinal);
        var known = Uses(Workflow("ci.yml")).Concat(Uses(release.Replace(job, "", StringComparison.Ordinal))).ToHashSet(StringComparer.Ordinal);
        Assert.All(Uses(job), action => Assert.Contains(action, known));
    }

    // A Windows packaging failure must not hold back the image, and the reverse.
    [Fact]
    public void TheWindowsPackagingJobAndThePackageJobDoNotWaitForEachOther()
    {
        var release = Workflow("release.yml");
        Assert.Empty(Needs(Job(release, "package-windows")));
        var job = Job(release, "package");
        Assert.DoesNotContain("package-windows", Needs(job));
        Assert.DoesNotContain("package-windows", job, StringComparison.Ordinal);
    }

    [Fact]
    public void CiDryRunsThePackagingScriptOnWindowsAndGatesOnIt()
    {
        var ci = Workflow("ci.yml");
        var job = Job(ci, "package-dry-run");

        Assert.Matches(@"(?m)^\s*runs-on:\s*windows-2025\s*$", job);
        Assert.Contains("./" + PackagingScript + " -Rid win-x64 -DryRun", job, StringComparison.Ordinal);
        Assert.Contains("package-dry-run", Needs(Job(ci, "ci")));
        Assert.Contains("needs.package-dry-run.result", Job(ci, "ci"), StringComparison.Ordinal);
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
