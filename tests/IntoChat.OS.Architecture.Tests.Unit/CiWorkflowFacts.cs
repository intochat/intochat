using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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

        // Single compilation job uploads packages, unit-test-artifacts, and compiled-artifacts
        var build = Job(ci, "build");
        Assert.Contains("dotnet build Intochat.OS.slnx", build, StringComparison.Ordinal);
        Assert.Contains("upload-artifact", build, StringComparison.Ordinal);

        // Architecture & unit tiers fan out in parallel with integration & E2E against the single build
        var testUnit = Job(ci, "test-unit");
        Assert.Contains("build", Needs(testUnit));
        Assert.Contains("download-artifact", testUnit, StringComparison.Ordinal);
        Assert.Contains("--test-modules", testUnit, StringComparison.Ordinal);
        Assert.Contains("--max-parallel-test-modules", testUnit, StringComparison.Ordinal);
        Assert.Contains(".Architecture.Tests.Unit.dll", testUnit, StringComparison.Ordinal);
        Assert.Contains(".Tests.Unit.dll", testUnit, StringComparison.Ordinal);

        // Integration & E2E test jobs run directly on compiled artifacts via MTP --test-modules
        var testIntegrationOs = Job(ci, "test-integration-os");
        Assert.Contains("build", Needs(testIntegrationOs));
        Assert.Contains("--test-modules", testIntegrationOs, StringComparison.Ordinal);
        Assert.Contains("download-artifact", testIntegrationOs, StringComparison.Ordinal);
        Assert.Contains("azurite", testIntegrationOs, StringComparison.Ordinal);
        Assert.Contains("postgres:16", testIntegrationOs, StringComparison.Ordinal);

        var testFramework = Job(ci, "test-framework");
        Assert.Contains("build", Needs(testFramework));
        Assert.Contains("--test-modules", testFramework, StringComparison.Ordinal);
        Assert.Contains("download-artifact", testFramework, StringComparison.Ordinal);
        Assert.Contains("azurite", testFramework, StringComparison.Ordinal);
        Assert.Contains("DigitalBrain*.Tests.Integration.dll", testFramework, StringComparison.Ordinal);
        Assert.Contains("DigitalBrain.Framework.Tests.E2E.dll", testFramework, StringComparison.Ordinal);

        var e2eOs = Job(ci, "e2e-os");
        Assert.Contains("build", Needs(e2eOs));
        Assert.Contains("--test-modules", e2eOs, StringComparison.Ordinal);
        Assert.Contains("INTOCHATOS__KERNEL_BINARY", e2eOs, StringComparison.Ordinal);
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
        Assert.Contains("build", needs);
        Assert.Contains("test-unit", needs);
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

        // .NET from global.json; Flutter pinned to the version ci tests the shell with; Microsoft.WinAppCLI installed for MSIX packing and az-sign.
        Assert.Contains("global-json-file: global.json", job, StringComparison.Ordinal);
        var flutterVersion = Regex.Match(Job(Workflow("ci.yml"), "flutter"), @"flutter-version:\s*'(?<v>[^']+)'");
        Assert.True(flutterVersion.Success, "ci flutter job must pin flutter-version");
        Assert.Contains($"flutter-version: '{flutterVersion.Groups["v"].Value}'", job, StringComparison.Ordinal);
        Assert.Contains("flutter pub get --enforce-lockfile", job, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WinAppCLI", job, StringComparison.Ordinal);
        Assert.Contains("id-token: write", job, StringComparison.Ordinal);

        // Conditional Azure Artifact Signing login and repo variable pass-through.
        Assert.Contains("vars.AZURE_ARTIFACT_SIGNING_ENDPOINT", job, StringComparison.Ordinal);
        Assert.Contains("vars.AZURE_ARTIFACT_SIGNING_ACCOUNT", job, StringComparison.Ordinal);
        Assert.Contains("vars.AZURE_ARTIFACT_SIGNING_PROFILE", job, StringComparison.Ordinal);
        Assert.Contains("vars.WINDOWS_PUBLISHER_SUBJECT", job, StringComparison.Ordinal);
        Assert.Contains("azure/login@v3", job, StringComparison.Ordinal);

        // Versioned archive, stable aliases, setup installer, MSIX + AppInstaller, checksums, and manifests go to the release the tag belongs to.
        var upload = Regex.Match(job, @"gh release upload [^\r\n]*");
        Assert.True(upload.Success, "package-windows must attach its output with gh release upload");
        Assert.Contains("\"${GITHUB_REF_NAME}\"", upload.Value, StringComparison.Ordinal);
        Assert.Contains("intochat-*-win-x64.zip", upload.Value, StringComparison.Ordinal);
        Assert.Contains("intochat-win-x64.zip", upload.Value, StringComparison.Ordinal);
        Assert.Contains("IntoChat-Setup-win-x64.exe", upload.Value, StringComparison.Ordinal);
        Assert.Contains("IntoChat-*-win-x64.msix", upload.Value, StringComparison.Ordinal);
        Assert.Contains("IntoChat-win-x64.msix", upload.Value, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.appinstaller", upload.Value, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", upload.Value, StringComparison.Ordinal);
        Assert.Contains("latest.json", upload.Value, StringComparison.Ordinal);
        Assert.Contains("winget/IntoChat.OS.yaml", upload.Value, StringComparison.Ordinal);

        // Only Azure OIDC login secrets (gated on AZURE_ARTIFACT_SIGNING_* repo vars), and only action versions another job already uses.
        var allowedSecrets = new HashSet<string>(StringComparer.Ordinal)
        {
            "AZURE_CLIENT_ID",
            "AZURE_TENANT_ID",
            "AZURE_SUBSCRIPTION_ID",
        };
        var usedSecrets = Regex.Matches(job, @"secrets\.([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.All(usedSecrets, secret => Assert.Contains(secret, allowedSecrets));
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

        Assert.Contains("downloaded-bundles/*.msix", publishChecksums, StringComparison.Ordinal);
        Assert.Contains("downloaded-bundles/*.appinstaller", publishChecksums, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", publishChecksums, StringComparison.Ordinal);
        Assert.Contains("gh release upload", publishChecksums, StringComparison.Ordinal);
    }

    // On Windows, starting bare "flutter.bat" with WorkingDirectory = uiDir makes cmd.exe expand %~dp0
    // to uiDir instead of the Flutter SDK bin directory, failing with "The system cannot find the path specified."
    [Fact]
    public void PackageScriptResolvesFlutterAndWinAppPathsBeforeInvoking()
    {
        var packageScript = File.ReadAllText(Path.Combine(Repository.Root, "eng", "package.cs"));

        Assert.Contains("ResolveFlutterCommand()", packageScript, StringComparison.Ordinal);
        Assert.Contains("ResolveWinAppCommand()", packageScript, StringComparison.Ordinal);
        Assert.Contains("ResolveOnPath(executable)", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("var flutterCmd = OperatingSystem.IsWindows()", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsPackagingDryRunListsExpectedArtifactsAndGeneratesCompliantMsixManifests()
    {
        var ct = TestContext.Current.CancellationToken;
        var manifestOut = Directory.CreateTempSubdirectory("intochat-msix-manifests-");
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Repository.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("eng/package.cs");
            startInfo.ArgumentList.Add("--platform");
            startInfo.ArgumentList.Add("win-x64");
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add("0.24.0");
            startInfo.ArgumentList.Add("--dry-run");
            startInfo.ArgumentList.Add("--emit-msix-manifests");
            startInfo.ArgumentList.Add(manifestOut.FullName);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(process.ExitCode == 0, $"eng/package.cs --dry-run failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");

            Assert.Contains("intochat-0.24.0-win-x64.zip", stdout, StringComparison.Ordinal);
            Assert.Contains("IntoChat-Setup-0.24.0-win-x64.exe", stdout, StringComparison.Ordinal);
            Assert.Contains("IntoChat-0.24.0-win-x64.msix", stdout, StringComparison.Ordinal);
            Assert.Contains("IntoChat-win-x64.msix", stdout, StringComparison.Ordinal);
            Assert.Contains("IntoChat.OS.appinstaller", stdout, StringComparison.Ordinal);
            Assert.Contains("SHA256SUMS.win-x64", stdout, StringComparison.Ordinal);
            Assert.Contains("SHA256SUMS", stdout, StringComparison.Ordinal);

            var manifestPath = Path.Combine(manifestOut.FullName, "AppxManifest.xml");
            Assert.True(File.Exists(manifestPath), "Expected AppxManifest.xml to be emitted.");

            var manifest = XDocument.Load(manifestPath);
            XNamespace appx = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            XNamespace uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
            XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
            XNamespace desktop6 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";
            XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

            var identity = manifest.Root?.Element(appx + "Identity");
            Assert.NotNull(identity);
            Assert.Equal("0.24.0.0", identity.Attribute("Version")?.Value);
            Assert.Equal("x64", identity.Attribute("ProcessorArchitecture")?.Value);

            var properties = manifest.Root?.Element(appx + "Properties");
            Assert.NotNull(properties);
            Assert.Equal("disabled", properties.Element(desktop6 + "FileSystemWriteVirtualization")?.Value);
            Assert.Equal("disabled", properties.Element(desktop6 + "RegistryWriteVirtualization")?.Value);
            Assert.Equal(@"Assets\StoreLogo.png", properties.Element(appx + "Logo")?.Value);

            var capabilities = manifest.Descendants(rescap + "Capability").Select(e => e.Attribute("Name")?.Value).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("runFullTrust", capabilities);
            Assert.Contains("unvirtualizedResources", capabilities);

            var aliases = manifest.Descendants(uap3 + "AppExecutionAlias")
                .Descendants(desktop + "ExecutionAlias")
                .Select(e => e.Attribute("Alias")?.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("intochat.exe", aliases);
            Assert.Contains("intochat-os.exe", aliases);

            var protocols = manifest.Descendants(uap + "Protocol").Select(e => e.Attribute("Name")?.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("intochat", protocols);

            var visualElements = manifest.Descendants(uap + "VisualElements").Single();
            Assert.Equal(@"Assets\Square44x44Logo.png", visualElements.Attribute("Square44x44Logo")?.Value);
            Assert.Equal(@"Assets\Square150x150Logo.png", visualElements.Attribute("Square150x150Logo")?.Value);

            var appInstallerPath = Path.Combine(manifestOut.FullName, "IntoChat.OS.appinstaller");
            Assert.True(File.Exists(appInstallerPath), "Expected IntoChat.OS.appinstaller to be emitted.");

            var appInstaller = XDocument.Load(appInstallerPath);
            XNamespace ai2021 = "http://schemas.microsoft.com/appx/appinstaller/2021";
            Assert.Equal(ai2021 + "AppInstaller", appInstaller.Root?.Name);
            Assert.Equal("0.24.0.0", appInstaller.Root?.Attribute("Version")?.Value);

            var mainPackage = appInstaller.Root?.Element(ai2021 + "MainPackage");
            Assert.NotNull(mainPackage);
            Assert.EndsWith("IntoChat-0.24.0-win-x64.msix", mainPackage.Attribute("Uri")?.Value, StringComparison.Ordinal);

            var updateSettings = appInstaller.Root?.Element(ai2021 + "UpdateSettings");
            Assert.NotNull(updateSettings);
            var onLaunch = updateSettings.Element(ai2021 + "OnLaunch");
            Assert.NotNull(onLaunch);
            Assert.Equal("true", onLaunch.Attribute("UpdateBlocksActivation")?.Value);
            Assert.Equal("true", onLaunch.Attribute("ShowPrompt")?.Value);
            Assert.NotNull(updateSettings.Element(ai2021 + "AutomaticBackgroundTask"));
        }
        finally
        {
            if (manifestOut.Exists)
            {
                manifestOut.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void LegacyMsixPackagerIsRetiredWithZeroDanglingReferences()
    {
        var localSetup = Repository.ProjectNamed("IntoChat.OS.LocalSetup");
        var retiredPath = Path.Combine(localSetup.Directory, "MsixPackager.cs");
        Assert.False(File.Exists(retiredPath), "Legacy MsixPackager.cs must remain deleted in favor of eng/package.cs.");

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".md", ".yml", ".yaml" };
        var offenders = new List<string>();

        var launcherPath = Path.Combine(Repository.Root, "intochat.cs");
        var launcherText = File.ReadAllText(launcherPath);
        if (launcherText.Contains("MsixPackager", StringComparison.Ordinal))
        {
            offenders.Add("intochat.cs");
        }

        Assert.Contains("package.cs", launcherText, StringComparison.Ordinal);
        Assert.Contains("--platform", launcherText, StringComparison.Ordinal);
        Assert.Contains("win-x64", launcherText, StringComparison.Ordinal);

        var candidateFiles = Repository.ProjectFiles("*.*", projects: project => !Repository.IsTestProject(project))
            .Concat(Repository.SourceFiles(Path.Combine(Repository.Root, "eng"), "*.*"))
            .Concat(Repository.SourceFiles(Path.Combine(Repository.Root, "docs"), "*.*"));

        foreach (var file in candidateFiles)
        {
            if (!extensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("MsixPackager", StringComparison.Ordinal))
            {
                offenders.Add(Repository.Relative(file));
            }
        }

        Assert.True(offenders.Count == 0, "Dangling references to retired MsixPackager found: " + string.Join(", ", offenders));
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
