using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Workflow guard rails that no run can prove while Actions is unavailable:
// Only ci.yml, release.yml, artcraft-images.yml, and codeql.yml exist in .github/workflows/; codeql.yml scans with
// .github/codeql/codeql-config.yml, which keeps generated web builds out of the scan.
// ci.yml runs unit, integration, e2e, and flutter jobs against a single build.
// release.yml builds images once and packages releases with no deploy scripts; deploy jobs live in the private cloud repository.
// No workflow references scripts/ directory or PACKAGE_TOKEN or DEPLOYMENT_VALUES_TOKEN.
// release.yml packages all 5 platforms with eng/package.cs and attaches versioned and stable release assets;
// ci.yml dry-runs eng/package.cs on Windows so it cannot rot between releases.
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
    public void OnlyCiReleaseArtcraftWarmAndCodeQlWorkflowsExist()
    {
        var workflows = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml")
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        string[] expected = ["artcraft-images.yml", "ci.yml", "codeql.yml", "release.yml"];
        Assert.Equal(expected, workflows);
    }

    [Fact]
    public void CiRunsUnitIntegrationE2EAndFlutterJobsAgainstSingleBuild()
    {
        var ci = Workflow("ci.yml");

        // Security & lint run in parallel with compilation
        var lint = Job(ci, "lint-and-security");
        Assert.Contains("gitleaks", lint, StringComparison.Ordinal);

        // Single compilation + architecture & unit tiers in build-and-unit job
        var build = Job(ci, "build-and-unit");
        Assert.Contains("dotnet build Intochat.OS.slnx", build, StringComparison.Ordinal);
        Assert.Contains("upload-artifact", build, StringComparison.Ordinal);
        Assert.Contains(".Architecture.Tests.Unit.dll", build, StringComparison.Ordinal);
        Assert.Contains(".Tests.Unit.dll", build, StringComparison.Ordinal);

        // Integration & E2E test jobs run directly on compiled artifacts via MTP --test-modules
        var testIntegrationOs = Job(ci, "test-integration-os");
        Assert.Contains("build-and-unit", Needs(testIntegrationOs));
        Assert.Contains("--test-modules", testIntegrationOs, StringComparison.Ordinal);
        Assert.Contains("download-artifact", testIntegrationOs, StringComparison.Ordinal);
        Assert.Contains("azurite", testIntegrationOs, StringComparison.Ordinal);
        Assert.Contains("postgres:16", testIntegrationOs, StringComparison.Ordinal);

        var testFramework = Job(ci, "test-framework");
        Assert.Contains("build-and-unit", Needs(testFramework));
        Assert.Contains("--test-modules", testFramework, StringComparison.Ordinal);
        Assert.Contains("download-artifact", testFramework, StringComparison.Ordinal);
        Assert.Contains("azurite", testFramework, StringComparison.Ordinal);
        Assert.Contains("DigitalBrain*.Tests.Integration.dll", testFramework, StringComparison.Ordinal);
        Assert.Contains("DigitalBrain.Framework.Tests.E2E.dll", testFramework, StringComparison.Ordinal);

        var e2eOs = Job(ci, "e2e-os");
        Assert.Contains("build-and-unit", Needs(e2eOs));
        Assert.Contains("--test-modules", e2eOs, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Tests.E2E.dll", e2eOs, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter-trait \"needs=artcraft\"", e2eOs, StringComparison.Ordinal);

        var flutter = Job(ci, "flutter");
        Assert.Contains("flutter test", flutter, StringComparison.Ordinal);

        // Gating ci job needs all parallel jobs
        var gatingCi = Job(ci, "ci");
        var needs = Needs(gatingCi);
        Assert.NotEmpty(needs);
        Assert.Contains("changes", needs);
        Assert.Contains("lint-and-security", needs);
        Assert.Contains("build-and-unit", needs);
        Assert.Contains("test-integration-os", needs);
        Assert.Contains("test-framework", needs);
        Assert.Contains("e2e-os", needs);
        Assert.Contains("flutter", needs);
    }

    // Azurite refuses a storage API version newer than its own release unless it is started with --skipApiVersionCheck.
    // A service container cannot pass that argument, so every job that uses Azurite starts it with docker run.
    [Theory]
    [InlineData("test-integration-os")]
    [InlineData("test-framework")]
    [InlineData("e2e-os")]
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
        Assert.Contains("acrintochattest.azurecr.io/sandbox:", package, StringComparison.Ordinal);
        Assert.DoesNotContain("acrintochattest.azurecr.io/os-ui:", package, StringComparison.Ordinal);
        Assert.DoesNotContain("acrintochattest.azurecr.io/os-sandbox:", package, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", package, StringComparison.Ordinal);
        Assert.Contains("\"sandboxImage\"", package, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Kernel.csproj", package, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Kernel/Dockerfile", package, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Sandbox/Dockerfile", package, StringComparison.Ordinal);

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

    private const string PackagingCommand = "dotnet run eng/package.cs --platform win-x64";
    private const string PackagingDryRunCommand = "dotnet run eng/package.cs --platform win-x64 --version 0.24.0 --dry-run";

    // The deploy facts above say "zero scripts": deploys go through Kernel install, and packaging all 5 platforms
    // runs through the unified cross-platform C# packaging script at eng/package.cs.
    [Fact]
    public void ReleasePackagesTheWindowsBundleOnAWindowsRunnerAndAttachesItToTheRelease()
    {
        Assert.True(File.Exists(Path.Combine(Repository.Root, "eng", "package.cs")), "eng/package.cs must exist as the unified cross-platform packager");
        Assert.False(File.Exists(Path.Combine(Repository.Root, "eng", "release", "package-release.ps1")), "package-release.ps1 must be retired in favor of eng/package.cs");
        Assert.False(File.Exists(Path.Combine(Repository.Root, "eng", "release", "package-release.sh")), "package-release.sh must be retired in favor of eng/package.cs");
        var release = Workflow("release.yml");
        var job = Job(release, "package-windows");

        Assert.Matches(@"(?m)^\s*runs-on:\s*windows-2025\s*$", job);
        Assert.Matches(@"(?m)^\s*timeout-minutes:\s*\d+\s*$", job);
        Assert.Contains(PackagingCommand, job, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-flutter", job, StringComparison.Ordinal);
        Assert.DoesNotContain("--dry-run", job, StringComparison.Ordinal);

        // The same guards as the Linux package job: ci passed for this commit and the tag is the VERSION.
        Assert.Contains("check-runs", job, StringComparison.Ordinal);
        Assert.Contains("does not match VERSION", job, StringComparison.Ordinal);

        // .NET from global.json; Flutter pinned to the version ci tests the shell with.
        Assert.Contains("global-json-file: global.json", job, StringComparison.Ordinal);
        var flutterVersion = Regex.Match(Job(Workflow("ci.yml"), "flutter"), @"flutter-version:\s*'(?<v>[^']+)'");
        Assert.True(flutterVersion.Success, "ci flutter job must pin flutter-version");
        Assert.Contains($"flutter-version: '{flutterVersion.Groups["v"].Value}'", job, StringComparison.Ordinal);
        Assert.Contains("flutter pub get --enforce-lockfile", job, StringComparison.Ordinal);

        // Versioned archive, stable aliases, installer, checksums, and manifests go to the release the tag belongs to.
        var upload = Regex.Match(job, @"gh release upload [^\r\n]*");
        Assert.True(upload.Success, "package-windows must attach its output with gh release upload");
        Assert.Contains("\"${GITHUB_REF_NAME}\"", upload.Value, StringComparison.Ordinal);
        Assert.Contains("intochat-*-win-x64.zip", upload.Value, StringComparison.Ordinal);
        Assert.Contains("intochat-win-x64.zip", upload.Value, StringComparison.Ordinal);
        Assert.Contains("IntoChat-Setup-win-x64.exe", upload.Value, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", upload.Value, StringComparison.Ordinal);
        Assert.Contains("latest.json", upload.Value, StringComparison.Ordinal);
        Assert.Contains("winget/IntoChat.OS.yaml", upload.Value, StringComparison.Ordinal);

        // Only the repository token, and only action versions another job already uses.
        Assert.DoesNotContain("secrets.", job, StringComparison.Ordinal);
        var known = Uses(Workflow("ci.yml")).Concat(Uses(release.Replace(job, "", StringComparison.Ordinal))).ToHashSet(StringComparer.Ordinal);
        Assert.All(Uses(job), action => Assert.Contains(action, known));
    }

    [Fact]
    public void ReleasePackagesAllFivePlatformsWithUnifiedPackageScript()
    {
        var release = Workflow("release.yml");
        var packageScript = File.ReadAllText(Path.Combine(Repository.Root, "eng", "package.cs"));

        // Unified packager bundles IntoChat.OS.Sandbox under unithost for all 3 desktop platforms.
        Assert.Contains("IntoChat.OS.Sandbox.csproj", packageScript, StringComparison.Ordinal);
        Assert.Contains("unithost", packageScript, StringComparison.Ordinal);

        var platformJobs = new (string JobName, string PlatformArg, string StableAlias)[]
        {
            ("package-windows", "--platform win-x64", "IntoChat-Setup-win-x64.exe"),
            ("package-linux", "--platform linux-x64", "intochat-linux-amd64.deb"),
            ("package-macos", "--platform osx-arm64", "IntoChat-macos-arm64.dmg"),
            ("package-android", "--platform android", "intochat-android.apk"),
            ("package-ios", "--platform ios", "intochat-ios-unsigned.ipa"),
        };

        foreach (var (jobName, platformArg, stableAlias) in platformJobs)
        {
            var job = Job(release, jobName);
            Assert.Contains($"dotnet run eng/package.cs {platformArg}", job, StringComparison.Ordinal);
            Assert.Contains("global-json-file: global.json", job, StringComparison.Ordinal);
            Assert.Contains(stableAlias, job, StringComparison.Ordinal);
        }

        var publishChecksums = Job(release, "publish-checksums");
        var needs = Needs(publishChecksums);
        foreach (var (jobName, _, _) in platformJobs)
        {
            Assert.Contains(jobName, needs);
        }

        Assert.Contains("SHA256SUMS", publishChecksums, StringComparison.Ordinal);
        Assert.Contains("gh release upload", publishChecksums, StringComparison.Ordinal);
    }

    // On Windows, starting bare "flutter.bat" with WorkingDirectory = uiDir makes cmd.exe expand %~dp0
    // to uiDir instead of the Flutter SDK bin directory, failing with "The system cannot find the path specified."
    [Fact]
    public void PackageScriptResolvesFlutterPathBeforeInvoking()
    {
        var packageScript = File.ReadAllText(Path.Combine(Repository.Root, "eng", "package.cs"));

        Assert.Contains("ResolveFlutterCommand()", packageScript, StringComparison.Ordinal);
        Assert.Contains("ResolveOnPath(executable)", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("var flutterCmd = OperatingSystem.IsWindows()", packageScript, StringComparison.Ordinal);
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
        Assert.Contains(PackagingDryRunCommand, job, StringComparison.Ordinal);
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
