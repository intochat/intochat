#!/usr/bin/env -S dotnet --
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Unified cross-platform IntoChat OS release packager (https://intochat.io).
// Usage:
//   dotnet run eng/package.cs --platform <win-x64|linux-x64|osx-arm64|android|ios> --version <ver> [--output <dir>] [--dry-run] [--skip-flutter]

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

return await ReleasePackageScript.MainAsync(args, CancellationToken.None);

internal sealed record PackageOptions
{
    public const string DefaultPublicDomain = "intochat.io";
    public const string DefaultMsixName = "IntoChat.OS";
    public const string DefaultPublisherDisplayName = "IntoChat";
    public const string DefaultSignedPublisher = "CN=IntoChat";
    public const string DefaultDevPublisher = "CN=IntoChat Dev";
    public const string DefaultAppInstallerBaseUri = "https://github.com/intochat/intochat/releases/latest/download";

    public string? Platform { get; init; }
    public string? Version { get; init; }
    public string? OutputDir { get; init; }
    public string? RepoRoot { get; init; }
    public string Configuration { get; init; } = "Release";
    public string Channel { get; init; } = "stable";
    public string PublicDomain { get; init; } = ReadEnv("INTOCHAT_PUBLIC_DOMAIN") ?? DefaultPublicDomain;
    public bool DryRun { get; init; }
    public bool SkipFlutter { get; init; }
    public bool SkipMsix { get; init; }
    public bool DevCert { get; init; }
    public string? CertPath { get; init; }
    public string? CertPassword { get; init; }
    public int MsixBuild { get; init; }
    public string MsixName { get; init; } = DefaultMsixName;
    public string? Publisher { get; init; } = ReadEnv("WINDOWS_PUBLISHER_SUBJECT");
    public string PublisherDisplayName { get; init; } = DefaultPublisherDisplayName;
    public string AppInstallerBaseUri { get; init; } = ReadEnv("INTOCHAT_APPINSTALLER_BASE_URI") ?? DefaultAppInstallerBaseUri;
    public string? AzureSigningEndpoint { get; init; } = ReadEnv("AZURE_ARTIFACT_SIGNING_ENDPOINT");
    public string? AzureSigningAccount { get; init; } = ReadEnv("AZURE_ARTIFACT_SIGNING_ACCOUNT");
    public string? AzureSigningProfile { get; init; } = ReadEnv("AZURE_ARTIFACT_SIGNING_PROFILE");
    public string? EmitMsixManifestsDir { get; init; }
    public bool NativeAotKernel { get; init; } = true;
    public bool ShowHelp { get; init; }

    public bool HasAnyAzureArtifactSigning =>
        !string.IsNullOrWhiteSpace(AzureSigningEndpoint)
        || !string.IsNullOrWhiteSpace(AzureSigningAccount)
        || !string.IsNullOrWhiteSpace(AzureSigningProfile);

    public bool HasAzureArtifactSigning =>
        !string.IsNullOrWhiteSpace(AzureSigningEndpoint)
        && !string.IsNullOrWhiteSpace(AzureSigningAccount)
        && !string.IsNullOrWhiteSpace(AzureSigningProfile);

    public string ResolvePublisher()
        => !string.IsNullOrWhiteSpace(Publisher)
            ? Publisher.Trim()
            : HasAzureArtifactSigning
                ? DefaultSignedPublisher
                : DefaultDevPublisher;

    public string DescribeWindowsSigningMode()
    {
        if (HasAzureArtifactSigning)
        {
            return $"Azure Artifact Signing (winapp az-sign, account={AzureSigningAccount}, profile={AzureSigningProfile})";
        }

        if (DevCert)
        {
            return "local dev certificate (winapp cert generate + --cert)";
        }

        if (!string.IsNullOrWhiteSpace(CertPath))
        {
            return $"local PFX certificate (--cert {CertPath})";
        }

        return "unsigned";
    }

    public void ValidateWindowsSigningOptions()
    {
        if (MsixBuild is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentException($"--msix-build must be between 0 and {ushort.MaxValue} (got {MsixBuild}).");
        }

        if (HasAnyAzureArtifactSigning && !HasAzureArtifactSigning)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(AzureSigningEndpoint)) { missing.Add("AZURE_ARTIFACT_SIGNING_ENDPOINT"); }
            if (string.IsNullOrWhiteSpace(AzureSigningAccount)) { missing.Add("AZURE_ARTIFACT_SIGNING_ACCOUNT"); }
            if (string.IsNullOrWhiteSpace(AzureSigningProfile)) { missing.Add("AZURE_ARTIFACT_SIGNING_PROFILE"); }
            throw new InvalidOperationException(
                $"Incomplete Azure Artifact Signing configuration. Missing: {string.Join(", ", missing)}.");
        }

        if (DevCert && !string.IsNullOrWhiteSpace(CertPath))
        {
            throw new ArgumentException("--dev-cert cannot be combined with --cert.");
        }

        if (DevCert && HasAzureArtifactSigning)
        {
            throw new ArgumentException("--dev-cert cannot be combined with Azure Artifact Signing (AZURE_ARTIFACT_SIGNING_*).");
        }

        if (!string.IsNullOrWhiteSpace(CertPath) && HasAzureArtifactSigning)
        {
            throw new ArgumentException("--cert cannot be combined with Azure Artifact Signing (AZURE_ARTIFACT_SIGNING_*).");
        }
    }

    private static string? ReadEnv(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    public static PackageOptions Parse(string[] args)
    {
        var options = new PackageOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            options = arg.ToLowerInvariant() switch
            {
                "--platform" or "--rid" when i + 1 < args.Length => options with { Platform = args[++i].Trim().ToLowerInvariant() },
                "--version" when i + 1 < args.Length => options with { Version = args[++i].Trim() },
                "--output" or "--output-dir" or "-o" when i + 1 < args.Length => options with { OutputDir = args[++i].Trim() },
                "--repo-root" when i + 1 < args.Length => options with { RepoRoot = args[++i].Trim() },
                "--configuration" or "-c" when i + 1 < args.Length => options with { Configuration = args[++i].Trim() },
                "--channel" when i + 1 < args.Length => options with { Channel = args[++i].Trim() },
                "--public-domain" or "--domain" when i + 1 < args.Length => options with { PublicDomain = args[++i].Trim() },
                "--msix-build" or "--build" when i + 1 < args.Length => options with { MsixBuild = int.Parse(args[++i].Trim(), CultureInfo.InvariantCulture) },
                "--msix-name" when i + 1 < args.Length => options with { MsixName = args[++i].Trim() },
                "--publisher" when i + 1 < args.Length => options with { Publisher = args[++i].Trim() },
                "--publisher-display-name" when i + 1 < args.Length => options with { PublisherDisplayName = args[++i].Trim() },
                "--appinstaller-base-uri" or "--base-uri" when i + 1 < args.Length => options with { AppInstallerBaseUri = args[++i].Trim() },
                "--cert" or "--certificate-path" when i + 1 < args.Length => options with { CertPath = args[++i].Trim() },
                "--cert-password" or "--certificate-password" when i + 1 < args.Length => options with { CertPassword = args[++i] },
                "--az-sign-endpoint" when i + 1 < args.Length => options with { AzureSigningEndpoint = args[++i].Trim() },
                "--az-sign-account" when i + 1 < args.Length => options with { AzureSigningAccount = args[++i].Trim() },
                "--az-sign-profile" when i + 1 < args.Length => options with { AzureSigningProfile = args[++i].Trim() },
                "--emit-msix-manifests" when i + 1 < args.Length => options with { EmitMsixManifestsDir = args[++i].Trim() },
                "--dry-run" => options with { DryRun = true },
                "--skip-flutter" or "--skip-shell" => options with { SkipFlutter = true },
                "--skip-msix" => options with { SkipMsix = true },
                "--dev-cert" => options with { DevCert = true },
                "--no-aot-kernel" => options with { NativeAotKernel = false },
                "--help" or "-h" or "-?" => options with { ShowHelp = true },
                _ => throw new ArgumentException($"Unknown argument '{arg}'."),
            };
        }

        return options;
    }
}

internal static class ReleasePackageScript
{
    private static readonly string[] SupportedPlatforms = ["win-x64", "linux-x64", "osx-arm64", "android", "ios"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static async Task<int> MainAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var options = PackageOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage(Console.Out);
                return 0;
            }

            if (string.IsNullOrWhiteSpace(options.Platform) || !SupportedPlatforms.Contains(options.Platform, StringComparer.Ordinal))
            {
                await Console.Error.WriteLineAsync($"Error: --platform must be one of: {string.Join(", ", SupportedPlatforms)}.".AsMemory(), cancellationToken);
                PrintUsage(Console.Error);
                return 2;
            }

            var repoRoot = string.IsNullOrWhiteSpace(options.RepoRoot)
                ? FindRepoRoot()
                : Path.GetFullPath(options.RepoRoot);

            var version = await ResolveVersionAsync(repoRoot, options.Version, cancellationToken);
            var outputDir = string.IsNullOrWhiteSpace(options.OutputDir)
                ? Path.Combine(repoRoot, "artifacts", "release")
                : Path.GetFullPath(options.OutputDir);

            await Console.Out.WriteLineAsync($"IntoChat OS Release Packager v{version} ({options.Platform})".AsMemory(), cancellationToken);

            if (options.Platform == "win-x64")
            {
                options.ValidateWindowsSigningOptions();
                if (!options.SkipMsix)
                {
                    _ = ToMsixVersion(version, options.MsixBuild);
                }
            }

            var inputs = AssertInputs(repoRoot, options.Platform, options.SkipFlutter, options.SkipMsix);

            if (options.Platform == "win-x64" && !string.IsNullOrWhiteSpace(options.EmitMsixManifestsDir))
            {
                await WriteMsixManifestsAsync(Path.GetFullPath(options.EmitMsixManifestsDir), version, options, cancellationToken);
            }

            if (options.DryRun)
            {
                await PrintDryRunAsync(options, version, outputDir, inputs);
                return 0;
            }

            Directory.CreateDirectory(outputDir);
            var stageRoot = Path.Combine(Path.GetTempPath(), $"intochat-pkg-{options.Platform}-{Guid.NewGuid():N}"[..36]);
            Directory.CreateDirectory(stageRoot);

            try
            {
                switch (options.Platform)
                {
                    case "win-x64":
                        await PackageWindowsAsync(repoRoot, stageRoot, outputDir, version, options, cancellationToken);
                        break;
                    case "linux-x64":
                        await PackageLinuxAsync(repoRoot, stageRoot, outputDir, version, options, cancellationToken);
                        break;
                    case "osx-arm64":
                        await PackageMacOsAsync(repoRoot, stageRoot, outputDir, version, options, cancellationToken);
                        break;
                    case "android":
                        await PackageAndroidAsync(repoRoot, outputDir, version, options, cancellationToken);
                        break;
                    case "ios":
                        await PackageIosAsync(repoRoot, stageRoot, outputDir, version, options, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported platform '{options.Platform}'.");
                }

                return 0;
            }
            finally
            {
                TryDeleteDirectory(stageRoot);
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"package: {ex.Message}".AsMemory(), cancellationToken);
            return 1;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: dotnet run eng/package.cs --platform <win-x64|linux-x64|osx-arm64|android|ios> [--version <ver>] [--output <dir>] [--dry-run] [--skip-flutter] [--skip-msix] [--dev-cert] [--cert <pfx>] [--msix-build <n>] [--publisher <dn>] [--emit-msix-manifests <dir>]");
    }

    private static async Task<string> ResolveVersionAsync(string repoRoot, string? requestedVersion, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            return requestedVersion.Trim();
        }

        var versionFile = Path.Combine(repoRoot, "VERSION");
        if (!File.Exists(versionFile))
        {
            throw new InvalidOperationException($"VERSION file not found at '{versionFile}'.");
        }

        var version = (await File.ReadAllTextAsync(versionFile, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"VERSION file at '{versionFile}' is empty.");
        }

        return version;
    }

    private static string[] AssertInputs(string repoRoot, string platform, bool skipFlutter, bool skipMsix)
    {
        var inputs = new List<string>();
        var uiDir = Path.Combine(repoRoot, "ui");

        if (platform is "win-x64" or "linux-x64" or "osx-arm64")
        {
            inputs.Add(Path.Combine(repoRoot, "src", "IntoChat.OS.Desktop", "IntoChat.OS.Desktop.csproj"));
            inputs.Add(Path.Combine(repoRoot, "src", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.csproj"));
            inputs.Add(Path.Combine(repoRoot, "src", "IntoChat.OS.Sandbox", "IntoChat.OS.Sandbox.csproj"));

            if (platform == "win-x64")
            {
                inputs.Add(Path.Combine(repoRoot, "eng", "release", "winget", "IntoChat.OS.yaml"));
                inputs.Add(Path.Combine(repoRoot, "eng", "release", "windows", "IntoChat-DevCert.cer"));
                if (!skipMsix)
                {
                    inputs.Add(Path.Combine(uiDir, "web", "icons", "Icon-512.png"));
                }
            }
            else if (platform == "osx-arm64")
            {
                var brewTemplate = Path.Combine(repoRoot, "eng", "release", "homebrew", "intochat.rb");
                if (File.Exists(brewTemplate))
                {
                    inputs.Add(brewTemplate);
                }
            }

            if (!skipFlutter)
            {
                inputs.Add(Path.Combine(uiDir, "pubspec.yaml"));
                if (platform == "win-x64")
                {
                    inputs.Add(Path.Combine(uiDir, "windows", "CMakeLists.txt"));
                }
                else if (platform == "linux-x64")
                {
                    inputs.Add(Path.Combine(uiDir, "linux", "CMakeLists.txt"));
                }
                else if (platform == "osx-arm64")
                {
                    inputs.Add(Path.Combine(uiDir, "macos", "Runner.xcodeproj", "project.pbxproj"));
                }
            }
        }
        else if (platform == "android")
        {
            inputs.Add(Path.Combine(uiDir, "pubspec.yaml"));
            inputs.Add(Path.Combine(uiDir, "android", "app", "build.gradle.kts"));
        }
        else if (platform == "ios")
        {
            inputs.Add(Path.Combine(uiDir, "pubspec.yaml"));
            inputs.Add(Path.Combine(uiDir, "ios", "Runner.xcodeproj", "project.pbxproj"));
        }

        var missing = inputs.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Required packaging inputs not found: {string.Join(", ", missing)}");
        }

        return [.. inputs];
    }

    private static async Task PrintDryRunAsync(PackageOptions options, string version, string outputDir, string[] inputs)
    {
        foreach (var found in inputs)
        {
            await StepAsync($"[DryRun] Found {found}");
        }

        switch (options.Platform)
        {
            case "win-x64":
                var kernelMode = options.NativeAotKernel ? "Native AOT" : "ReadyToRun";
                await StepAsync($"[DryRun] Would stage IntoChat.OS.Desktop, os/IntoChat.OS.Kernel ({kernelMode}), unithost/IntoChat.OS.Sandbox, and Flutter Windows shell");
                if (options.SkipMsix)
                {
                    await StepAsync($"[DryRun] Would emit intochat-{version}-win-x64.zip, intochat-win-x64.zip, IntoChat-Setup-{version}-win-x64.exe, IntoChat-Setup-win-x64.exe, IntoChat-DevCert.cer, latest.json, winget/IntoChat.OS.yaml, SHA256SUMS.win-x64, and SHA256SUMS in {outputDir} (domain: {options.PublicDomain}; MSIX skipped via --skip-msix; signing: {options.DescribeWindowsSigningMode()})");
                }
                else
                {
                    var msixVersion = ToMsixVersion(version, options.MsixBuild);
                    var publisher = options.ResolvePublisher();
                    await StepAsync($"[DryRun] Would pack MSIX via winapp CLI (version: {msixVersion}, publisher: {publisher}, signing: {options.DescribeWindowsSigningMode()})");
                    await StepAsync($"[DryRun] Would emit intochat-{version}-win-x64.zip, intochat-win-x64.zip, IntoChat-Setup-{version}-win-x64.exe, IntoChat-Setup-win-x64.exe, IntoChat-{version}-win-x64.msix, IntoChat-win-x64.msix, {options.MsixName}.appinstaller, IntoChat-DevCert.cer, latest.json, winget/IntoChat.OS.yaml, SHA256SUMS.win-x64, and SHA256SUMS in {outputDir} (domain: {options.PublicDomain})");
                }
                break;
            case "linux-x64":
                await StepAsync("[DryRun] Would stage IntoChat.OS.Desktop, os/IntoChat.OS.Kernel, unithost/IntoChat.OS.Sandbox, and Flutter Linux shell");
                await StepAsync($"[DryRun] Would emit intochat-{version}-linux-x64.tar.gz, intochat-linux-x64.tar.gz, intochat-{version}-linux-amd64.deb, intochat-linux-amd64.deb, and SHA256SUMS.linux-x64 in {outputDir}");
                break;
            case "osx-arm64":
                await StepAsync("[DryRun] Would stage IntoChat.OS.Desktop, os/IntoChat.OS.Kernel, unithost/IntoChat.OS.Sandbox, and Flutter macOS shell");
                await StepAsync($"[DryRun] Would bundle IntoChat.app and emit intochat-{version}-osx-arm64.tar.gz, intochat-osx-arm64.tar.gz, IntoChat-{version}-macos-arm64.dmg, IntoChat-macos-arm64.dmg, homebrew/intochat.rb, and SHA256SUMS.osx-arm64 in {outputDir}");
                break;
            case "android":
                await StepAsync($"[DryRun] Would build Android release APK and emit intochat-{version}-android.apk, intochat-android.apk, and SHA256SUMS.android in {outputDir}");
                break;
            case "ios":
                await StepAsync($"[DryRun] Would build unsigned iOS IPA and emit intochat-{version}-ios-unsigned.ipa, intochat-ios-unsigned.ipa, and SHA256SUMS.ios in {outputDir}");
                break;
        }
    }

    private static async Task<string> StageDesktopBundleAsync(
        string repoRoot,
        string stageRoot,
        string rid,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var isWindows = rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase);
        var bundleDir = Path.Combine(stageRoot, "bundle");
        var osDir = Path.Combine(bundleDir, "os");
        var unitHostDir = Path.Combine(bundleDir, "unithost");
        var shellDir = Path.Combine(bundleDir, "shell");

        Directory.CreateDirectory(bundleDir);
        Directory.CreateDirectory(osDir);
        Directory.CreateDirectory(unitHostDir);
        Directory.CreateDirectory(shellDir);

        var desktopProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Desktop", "IntoChat.OS.Desktop.csproj");
        var kernelProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.csproj");
        var sandboxProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Sandbox", "IntoChat.OS.Sandbox.csproj");
        var uiDir = Path.Combine(repoRoot, "ui");

        if (isWindows)
        {
            await StepAsync($"Publishing IntoChat.OS.Desktop (Native AOT, {rid})...");
            RunChecked("dotnet", [
                "publish", desktopProj,
                "-c", options.Configuration,
                "-r", rid,
                "-o", bundleDir,
                "-p:CodeGraphRefresh=false",
                "-p:DebugType=none",
                "--nologo",
            ]);

            const string cliShim = "@echo off\r\n\"%~dp0IntoChat.OS.Desktop.exe\" %*\r\n";
            await File.WriteAllTextAsync(Path.Combine(bundleDir, "intochat.cmd"), cliShim, Encoding.ASCII, cancellationToken);
        }
        else
        {
            await StepAsync($"Publishing IntoChat.OS.Desktop (Self-Contained SingleFile, {rid})...");
            RunChecked("dotnet", [
                "publish", desktopProj,
                "-c", options.Configuration,
                "-r", rid,
                "--self-contained",
                "-p:PublishAot=false",
                "-p:PublishSingleFile=true",
                "-p:CodeGraphRefresh=false",
                "-p:DebugType=none",
                "-o", bundleDir,
                "--nologo",
            ]);

            const string posixShim = "#!/bin/sh\nSCRIPT_DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\nexec \"$SCRIPT_DIR/IntoChat.OS.Desktop\" \"$@\"\n";
            var shimPath = Path.Combine(bundleDir, "intochat");
            await File.WriteAllTextAsync(shimPath, posixShim, Utf8NoBom, cancellationToken);
            SetExecutableIfPosix(Path.Combine(bundleDir, "IntoChat.OS.Desktop"));
            SetExecutableIfPosix(shimPath);
        }

        if (isWindows && options.NativeAotKernel)
        {
            await StepAsync($"Publishing IntoChat.OS.Kernel (Native AOT, {rid})...");
            RunChecked("dotnet", [
                "publish", kernelProj,
                "-c", options.Configuration,
                "-r", rid,
                "--self-contained",
                "-o", osDir,
                "-p:IntoChatOSAot=true",
                "-p:CodeGraphRefresh=false",
                "-p:DebugType=none",
                "--nologo",
            ]);
        }
        else
        {
            await StepAsync($"Publishing IntoChat.OS.Kernel (Self-Contained, {rid})...");
            RunChecked("dotnet", [
                "publish", kernelProj,
                "-c", options.Configuration,
                "-r", rid,
                "--self-contained",
                "-o", osDir,
                "-p:CodeGraphRefresh=false",
                "-p:DebugType=none",
                "--nologo",
            ]);
        }

        if (!isWindows)
        {
            SetExecutableIfPosix(Path.Combine(osDir, "IntoChat.OS.Kernel"));
        }

        // All 3 desktop platforms bundle IntoChat.OS.Sandbox under unithost/ alongside os/IntoChat.OS.Kernel.
        await StepAsync($"Publishing IntoChat.OS.Sandbox (UnitHost, {rid})...");
        RunChecked("dotnet", [
            "publish", sandboxProj,
            "-c", options.Configuration,
            "-r", rid,
            "--self-contained",
            "-o", unitHostDir,
            "-p:PublishReadyToRun=true",
            "-p:CodeGraphRefresh=false",
            "-p:DebugType=none",
            "--nologo",
        ]);

        if (isWindows)
        {
            const string unitHostShim = "@echo off\r\n\"%~dp0IntoChat.OS.Sandbox.exe\" --unit-host %*\r\n";
            await File.WriteAllTextAsync(Path.Combine(unitHostDir, "IntoChat.OS.UnitHost.cmd"), unitHostShim, Encoding.ASCII, cancellationToken);
        }
        else
        {
            const string unitHostPosixShim = "#!/bin/sh\nSCRIPT_DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\nexec \"$SCRIPT_DIR/IntoChat.OS.Sandbox\" --unit-host \"$@\"\n";
            var unitHostShimPath = Path.Combine(unitHostDir, "IntoChat.OS.UnitHost");
            await File.WriteAllTextAsync(unitHostShimPath, unitHostPosixShim, Utf8NoBom, cancellationToken);
            SetExecutableIfPosix(Path.Combine(unitHostDir, "IntoChat.OS.Sandbox"));
            SetExecutableIfPosix(unitHostShimPath);
        }

        if (!options.SkipFlutter)
        {
            await BuildFlutterDesktopShellAsync(uiDir, shellDir, rid);
        }

        return bundleDir;
    }

    private static async Task BuildFlutterDesktopShellAsync(string uiDir, string shellDir, string rid)
    {
        var flutterCmd = ResolveFlutterCommand();
        if (rid == "win-x64")
        {
            await StepAsync("Building Flutter Windows shell...");
            var runnerOut = Path.Combine(uiDir, "build", "windows", "x64", "runner", "Release");
            RunChecked(flutterCmd, ["build", "windows", "--release"], workingDirectory: uiDir);
            if (!Directory.Exists(runnerOut))
            {
                throw new InvalidOperationException($"Flutter Windows output directory not found at '{runnerOut}'.");
            }

            CopyDirectory(runnerOut, shellDir);
        }
        else if (rid == "linux-x64")
        {
            await StepAsync("Building Flutter Linux shell...");
            RunChecked(flutterCmd, ["build", "linux", "--release"], workingDirectory: uiDir);
            var linuxOut = Path.Combine(uiDir, "build", "linux", "x64", "release", "bundle");
            if (!Directory.Exists(linuxOut))
            {
                throw new InvalidOperationException($"Flutter Linux output directory not found at '{linuxOut}'.");
            }

            CopyDirectoryPreservingLinks(linuxOut, shellDir);
            SetExecutableIfPosix(Path.Combine(shellDir, "intochat_os_flutter"));
        }
        else if (rid == "osx-arm64")
        {
            await StepAsync("Building Flutter macOS shell...");
            RunChecked(flutterCmd, ["build", "macos", "--release"], workingDirectory: uiDir);
            var appBundle = Path.Combine(uiDir, "build", "macos", "Build", "Products", "Release", "intochat_os_flutter.app");
            if (!Directory.Exists(appBundle))
            {
                throw new InvalidOperationException($"Flutter macOS app bundle not found at '{appBundle}'.");
            }

            var targetApp = Path.Combine(shellDir, "intochat_os_flutter.app");
            CopyDirectoryPreservingLinks(appBundle, targetApp);
            SetExecutableIfPosix(Path.Combine(targetApp, "Contents", "MacOS", "intochat_os_flutter"));
        }
    }

    private static async Task PackageWindowsAsync(
        string repoRoot,
        string stageRoot,
        string outputDir,
        string version,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var winappPath = ResolveWinAppCommand();
        if (options.HasAzureArtifactSigning && winappPath is null)
        {
            throw new InvalidOperationException(
                "winapp.exe not found on PATH; Microsoft.WinAppCli is required for Azure Artifact Signing (winapp az-sign).");
        }

        if (!options.SkipMsix && OperatingSystem.IsWindows() && winappPath is null)
        {
            throw new InvalidOperationException(
                "winapp.exe not found on PATH. Install Microsoft.WinAppCli (winget install --id Microsoft.WinAppCli --exact --source winget) or pass --skip-msix.");
        }

        string? azSignMetadataPath = null;
        if (options.HasAzureArtifactSigning)
        {
            azSignMetadataPath = await WriteAzureSigningMetadataAsync(stageRoot, options, cancellationToken);
        }

        var bundleDir = await StageDesktopBundleAsync(repoRoot, stageRoot, "win-x64", options, cancellationToken);

        // Sign internal executables inside the staged bundle before producing .zip, Setup.exe, and .msix
        // so every distribution channel carries authenticated signatures.
        if (azSignMetadataPath is not null && winappPath is not null)
        {
            await SignWindowsBundleExecutablesAsync(winappPath, azSignMetadataPath, bundleDir, options.SkipFlutter);
        }

        var versionedZip = $"intochat-{version}-win-x64.zip";
        var stableZip = "intochat-win-x64.zip";
        var versionedZipPath = Path.Combine(outputDir, versionedZip);
        var stableZipPath = Path.Combine(outputDir, stableZip);

        if (File.Exists(versionedZipPath))
        {
            File.Delete(versionedZipPath);
        }

        await StepAsync($"Creating {versionedZip} and {stableZip}...");
        ZipFile.CreateFromDirectory(bundleDir, versionedZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Copy(versionedZipPath, stableZipPath, overwrite: true);

        var stableSetup = "IntoChat-Setup-win-x64.exe";
        var versionedSetup = $"IntoChat-Setup-{version}-win-x64.exe";
        var stableSetupPath = Path.Combine(outputDir, stableSetup);
        var versionedSetupPath = Path.Combine(outputDir, versionedSetup);

        await BuildWindowsInstallerAsync(repoRoot, stageRoot, bundleDir, versionedZipPath, outputDir, version, stableSetupPath, cancellationToken);
        if (azSignMetadataPath is not null && winappPath is not null)
        {
            await StepAsync($"Signing {stableSetup} via winapp az-sign...");
            RunChecked(winappPath, ["az-sign", stableSetupPath, "--metadata-file", azSignMetadataPath]);
        }

        File.Copy(stableSetupPath, versionedSetupPath, overwrite: true);

        var zipSha256 = ComputeValidatedSha256(versionedZipPath);
        var checksums = new List<(string FileName, string Sha256)>
        {
            (versionedZip, zipSha256),
            (stableZip, ComputeValidatedSha256(stableZipPath)),
            (versionedSetup, ComputeValidatedSha256(versionedSetupPath)),
            (stableSetup, ComputeValidatedSha256(stableSetupPath)),
        };

        string? versionedMsix = null;
        string? msixSha256 = null;
        string? appInstallerFileName = null;

        if (!options.SkipMsix)
        {
            if (winappPath is not null)
            {
                var msixResult = await BuildWindowsMsixAsync(
                    repoRoot,
                    stageRoot,
                    bundleDir,
                    outputDir,
                    version,
                    options,
                    winappPath,
                    azSignMetadataPath,
                    cancellationToken);

                versionedMsix = msixResult.VersionedMsixFileName;
                msixSha256 = msixResult.VersionedMsixSha256;
                appInstallerFileName = msixResult.AppInstallerFileName;

                checksums.Add((msixResult.VersionedMsixFileName, msixResult.VersionedMsixSha256));
                checksums.Add((msixResult.StableMsixFileName, msixResult.StableMsixSha256));
                checksums.Add((msixResult.AppInstallerFileName, msixResult.AppInstallerSha256));
            }
            else
            {
                await StepAsync("Skipping .msix generation (winapp CLI is only available on Windows).");
            }
        }
        else
        {
            await StepAsync("Skipping .msix generation (--skip-msix was specified).");
        }

        const string devCertFileName = "IntoChat-DevCert.cer";
        var devCertSourcePath = Path.Combine(repoRoot, "eng", "release", "windows", devCertFileName);
        var devCertDestPath = Path.Combine(outputDir, devCertFileName);
        File.Copy(devCertSourcePath, devCertDestPath, overwrite: true);
        checksums.Add((devCertFileName, ComputeValidatedSha256(devCertDestPath)));

        await WriteSha256SumsFileAsync(Path.Combine(outputDir, "SHA256SUMS.win-x64"), checksums, cancellationToken);
        await WriteSha256SumsFileAsync(Path.Combine(outputDir, "SHA256SUMS"), checksums, cancellationToken);

        await WriteWindowsReleaseManifestsAsync(
            repoRoot,
            outputDir,
            version,
            options.Channel,
            options.PublicDomain,
            versionedZip,
            zipSha256,
            versionedMsix,
            msixSha256,
            appInstallerFileName,
            options.AppInstallerBaseUri,
            cancellationToken);

        await StepAsync($"Windows packaging complete in {outputDir}.");
    }

    private sealed record MsixBuildResult(
        string VersionedMsixFileName,
        string VersionedMsixSha256,
        string StableMsixFileName,
        string StableMsixSha256,
        string AppInstallerFileName,
        string AppInstallerSha256);

    private static async Task<string> WriteAzureSigningMetadataAsync(
        string stageRoot,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var metadata = new JsonObject
        {
            ["Endpoint"] = options.AzureSigningEndpoint,
            ["CodeSigningAccountName"] = options.AzureSigningAccount,
            ["CertificateProfileName"] = options.AzureSigningProfile,
        };
        var metadataPath = Path.Combine(stageRoot, "az-sign-metadata.json");
        var json = metadata.ToJsonString(IndentedJson).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        await File.WriteAllTextAsync(metadataPath, json, Utf8NoBom, cancellationToken);
        return metadataPath;
    }

    private static async Task SignWindowsBundleExecutablesAsync(
        string winappPath,
        string metadataPath,
        string bundleDir,
        bool skipFlutter)
    {
        var targets = new List<string>
        {
            Path.Combine(bundleDir, "IntoChat.OS.Desktop.exe"),
            Path.Combine(bundleDir, "os", "IntoChat.OS.Kernel.exe"),
            Path.Combine(bundleDir, "unithost", "IntoChat.OS.Sandbox.exe"),
        };

        var flutterExe = Path.Combine(bundleDir, "shell", "intochat_os_flutter.exe");
        if (!skipFlutter || File.Exists(flutterExe))
        {
            targets.Add(flutterExe);
        }

        foreach (var target in targets)
        {
            if (!File.Exists(target))
            {
                throw new InvalidOperationException($"Expected Windows binary to sign was not found at '{target}'.");
            }

            await StepAsync($"Signing {Path.GetRelativePath(bundleDir, target)} via winapp az-sign...");
            RunChecked(winappPath, ["az-sign", target, "--metadata-file", metadataPath]);
        }
    }

    private static async Task<MsixBuildResult> BuildWindowsMsixAsync(
        string repoRoot,
        string stageRoot,
        string bundleDir,
        string outputDir,
        string version,
        PackageOptions options,
        string winappPath,
        string? azSignMetadataPath,
        CancellationToken cancellationToken)
    {
        var msixVersion = ToMsixVersion(version, options.MsixBuild);
        var publisher = options.ResolvePublisher();
        var versionedMsix = $"IntoChat-{version}-win-x64.msix";
        var stableMsix = "IntoChat-win-x64.msix";
        var appInstallerName = $"{options.MsixName}.appinstaller";

        var msixLayoutDir = Path.Combine(stageRoot, "msix-layout");
        CopyDirectory(bundleDir, msixLayoutDir);

        var manifestPath = Path.Combine(msixLayoutDir, "AppxManifest.xml");
        var manifestXml = RenderAppxManifest(options.MsixName, publisher, options.PublisherDisplayName, msixVersion);
        await File.WriteAllTextAsync(manifestPath, manifestXml, Utf8NoBom, cancellationToken);

        var sourceIcon = ResolveMsixSourceIcon(repoRoot);
        await StepAsync($"Generating MSIX visual assets from {Path.GetRelativePath(repoRoot, sourceIcon)}...");
        RunChecked(winappPath, ["manifest", "update-assets", sourceIcon, "--manifest", manifestPath]);

        var assetsDir = Path.Combine(msixLayoutDir, "Assets");
        foreach (var requiredLogo in (string[])["Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png"])
        {
            var logoPath = Path.Combine(assetsDir, requiredLogo);
            if (!File.Exists(logoPath))
            {
                throw new InvalidOperationException($"Expected MSIX visual asset '{logoPath}' was not generated.");
            }
        }

        string? packCertPath = null;
        if (options.DevCert)
        {
            var devCertPath = Path.Combine(outputDir, "devcert.pfx");
            await StepAsync($"Ensuring local development certificate at {devCertPath}...");
            RunChecked(winappPath, ["cert", "generate", "--manifest", manifestPath, "--output", devCertPath, "--if-exists", "skip"]);
            packCertPath = devCertPath;
        }
        else if (!string.IsNullOrWhiteSpace(options.CertPath))
        {
            packCertPath = Path.GetFullPath(options.CertPath);
            if (!File.Exists(packCertPath))
            {
                throw new InvalidOperationException($"Signing certificate not found at '{packCertPath}'.");
            }
        }

        var versionedMsixPath = Path.Combine(outputDir, versionedMsix);
        var stableMsixPath = Path.Combine(outputDir, stableMsix);
        if (File.Exists(versionedMsixPath))
        {
            File.Delete(versionedMsixPath);
        }

        var packArgs = new List<string>
        {
            "pack",
            msixLayoutDir,
            "--output",
            versionedMsixPath,
            "--skip-pri",
        };

        if (packCertPath is not null)
        {
            packArgs.Add("--cert");
            packArgs.Add(packCertPath);
            if (!string.IsNullOrWhiteSpace(options.CertPassword))
            {
                packArgs.Add("--cert-password");
                packArgs.Add(options.CertPassword);
            }
        }

        await StepAsync($"Packing {versionedMsix} via winapp CLI (v{msixVersion}, publisher: {publisher})...");
        RunChecked(winappPath, [.. packArgs]);

        if (!File.Exists(versionedMsixPath))
        {
            throw new InvalidOperationException($"winapp pack did not produce expected MSIX package '{versionedMsixPath}'.");
        }

        if (azSignMetadataPath is not null)
        {
            await StepAsync($"Signing {versionedMsix} via winapp az-sign...");
            RunChecked(winappPath, ["az-sign", versionedMsixPath, "--metadata-file", azSignMetadataPath]);
        }

        File.Copy(versionedMsixPath, stableMsixPath, overwrite: true);

        var appInstallerPath = Path.Combine(outputDir, appInstallerName);
        var appInstallerXml = RenderAppInstaller(
            options.MsixName,
            publisher,
            msixVersion,
            options.AppInstallerBaseUri,
            versionedMsix);
        await File.WriteAllTextAsync(appInstallerPath, appInstallerXml, Utf8NoBom, cancellationToken);

        return new MsixBuildResult(
            VersionedMsixFileName: versionedMsix,
            VersionedMsixSha256: ComputeValidatedSha256(versionedMsixPath),
            StableMsixFileName: stableMsix,
            StableMsixSha256: ComputeValidatedSha256(stableMsixPath),
            AppInstallerFileName: appInstallerName,
            AppInstallerSha256: ComputeValidatedSha256(appInstallerPath));
    }

    private static async Task WriteMsixManifestsAsync(
        string targetDir,
        string version,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDir);
        var msixVersion = ToMsixVersion(version, options.MsixBuild);
        var publisher = options.ResolvePublisher();
        var versionedMsix = $"IntoChat-{version}-win-x64.msix";

        var manifestXml = RenderAppxManifest(options.MsixName, publisher, options.PublisherDisplayName, msixVersion);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "AppxManifest.xml"), manifestXml, Utf8NoBom, cancellationToken);

        var appInstallerXml = RenderAppInstaller(
            options.MsixName,
            publisher,
            msixVersion,
            options.AppInstallerBaseUri,
            versionedMsix);
        await File.WriteAllTextAsync(Path.Combine(targetDir, $"{options.MsixName}.appinstaller"), appInstallerXml, Utf8NoBom, cancellationToken);
    }

    private static string ResolveMsixSourceIcon(string repoRoot)
    {
        string[] candidates =
        [
            Path.Combine(repoRoot, "ui", "web", "icons", "Icon-512.png"),
            Path.Combine(repoRoot, "ui", "macos", "Runner", "Assets.xcassets", "AppIcon.appiconset", "app_icon_512.png"),
            Path.Combine(repoRoot, "ui", "web", "icons", "Icon-192.png"),
        ];

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"No source PNG icon found for MSIX visual assets. Checked: {string.Join(", ", candidates)}");
    }

    private static string ToMsixVersion(string version, int buildNumber)
    {
        if (buildNumber is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(buildNumber), $"MSIX build number must be in 0..{ushort.MaxValue}.");
        }

        var match = Regex.Match(version.Trim(), @"^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$");
        if (!match.Success)
        {
            throw new InvalidOperationException($"VERSION '{version}' cannot be converted to a 4-part MSIX version (expected major.minor.patch).");
        }

        var major = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = ushort.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = ushort.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return $"{major}.{minor}.{patch}.{buildNumber}";
    }

    private static string RenderAppxManifest(
        string packageName,
        string publisher,
        string publisherDisplayName,
        string msixVersion)
    {
        var escapedName = SecurityElement.Escape(packageName);
        var escapedPublisher = SecurityElement.Escape(publisher);
        var escapedPublisherDisplayName = SecurityElement.Escape(publisherDisplayName);
        var escapedVersion = SecurityElement.Escape(msixVersion);

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
              xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
              xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              IgnorableNamespaces="uap uap3 desktop desktop6 rescap">
              <Identity Name="{escapedName}" Publisher="{escapedPublisher}" Version="{escapedVersion}" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>IntoChat OS</DisplayName>
                <PublisherDisplayName>{escapedPublisherDisplayName}</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
                <Description>Local-first AI operating system with Ino, live C# micro-apps, ArtCraft studios, and hardware-confined sandboxes.</Description>
                <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
                <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
              </Properties>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>
              <Resources>
                <Resource Language="en-us" />
              </Resources>
              <Applications>
                <Application Id="IntoChatOS" Executable="IntoChat.OS.Desktop.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements DisplayName="IntoChat OS" Description="IntoChat OS" BackgroundColor="transparent"
                                      Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
                  <Extensions>
                    <uap3:Extension Category="windows.appExecutionAlias" Executable="IntoChat.OS.Desktop.exe" EntryPoint="Windows.FullTrustApplication">
                      <uap3:AppExecutionAlias>
                        <desktop:ExecutionAlias Alias="intochat.exe" />
                        <desktop:ExecutionAlias Alias="intochat-os.exe" />
                      </uap3:AppExecutionAlias>
                    </uap3:Extension>
                    <uap:Extension Category="windows.protocol">
                      <uap:Protocol Name="intochat">
                        <uap:DisplayName>IntoChat OS</uap:DisplayName>
                      </uap:Protocol>
                    </uap:Extension>
                  </Extensions>
                </Application>
              </Applications>
              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
                <rescap:Capability Name="unvirtualizedResources" />
              </Capabilities>
            </Package>
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static string RenderAppInstaller(
        string packageName,
        string publisher,
        string msixVersion,
        string baseUri,
        string versionedMsixFileName)
    {
        var trimmedBaseUri = baseUri.TrimEnd('/');
        var escapedName = SecurityElement.Escape(packageName);
        var escapedPublisher = SecurityElement.Escape(publisher);
        var escapedVersion = SecurityElement.Escape(msixVersion);
        var escapedAppInstallerUri = SecurityElement.Escape($"{trimmedBaseUri}/{packageName}.appinstaller");
        var escapedPackageUri = SecurityElement.Escape($"{trimmedBaseUri}/{versionedMsixFileName}");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <AppInstaller
              xmlns="http://schemas.microsoft.com/appx/appinstaller/2021"
              Version="{escapedVersion}"
              Uri="{escapedAppInstallerUri}">
              <MainPackage
                Name="{escapedName}"
                Publisher="{escapedPublisher}"
                Version="{escapedVersion}"
                ProcessorArchitecture="x64"
                Uri="{escapedPackageUri}" />
              <UpdateSettings>
                <OnLaunch HoursBetweenUpdateChecks="24" ShowPrompt="true" UpdateBlocksActivation="true" />
                <AutomaticBackgroundTask />
              </UpdateSettings>
            </AppInstaller>
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static async Task BuildWindowsInstallerAsync(
        string repoRoot,
        string stageRoot,
        string bundleDir,
        string zipPath,
        string outputDir,
        string version,
        string setupOutputPath,
        CancellationToken cancellationToken)
    {
        var isccPath = FindInnoSetupCompiler();
        if (isccPath is not null)
        {
            await StepAsync($"Building Windows installer with Inno Setup ({isccPath})...");
            var iconPath = Path.Combine(repoRoot, "ui", "windows", "runner", "resources", "app_icon.ico");
            var iconDirective = File.Exists(iconPath) ? $"SetupIconFile={iconPath}\r\n" : string.Empty;
            var issPath = Path.Combine(stageRoot, "IntoChat-Setup.iss");
            var issContent = $$$"""
                [Setup]
                AppId={{B4E7F2A1-8C3D-4E5F-9A6B-1C2D3E4F5A6B}
                AppName=IntoChat OS
                AppVersion={{{version}}}
                AppVerName=IntoChat OS {{{version}}}
                AppPublisher=IntoChat
                AppPublisherURL=https://intochat.io
                AppSupportURL=https://github.com/intochat/intochat/issues
                AppUpdatesURL=https://github.com/intochat/intochat/releases
                DefaultDirName={localappdata}\Programs\IntoChat OS
                DefaultGroupName=IntoChat OS
                PrivilegesRequired=lowest
                OutputDir={{{outputDir}}}
                OutputBaseFilename=IntoChat-Setup-win-x64
                Compression=lzma2/normal
                SolidCompression=yes
                WizardStyle=modern
                ArchitecturesAllowed=x64
                ArchitecturesInstallIn64BitMode=x64
                CloseApplications=yes
                ChangesEnvironment=yes
                UninstallDisplayIcon={app}\IntoChat.OS.Desktop.exe
                {{{iconDirective}}}
                [Languages]
                Name: "english"; MessagesFile: "compiler:Default.isl"

                [Tasks]
                Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

                [Files]
                Source: "{{{bundleDir}}}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

                [Icons]
                Name: "{group}\IntoChat OS"; Filename: "{app}\IntoChat.OS.Desktop.exe"
                Name: "{group}\{cm:UninstallProgram,IntoChat OS}"; Filename: "{uninstallexe}"
                Name: "{autodesktop}\IntoChat OS"; Filename: "{app}\IntoChat.OS.Desktop.exe"; Tasks: desktopicon

                [Run]
                Filename: "{app}\IntoChat.OS.Desktop.exe"; Description: "{cm:LaunchProgram,IntoChat OS}"; Flags: nowait postinstall skipifsilent
                """;
            await File.WriteAllTextAsync(issPath, issContent, Utf8NoBom, cancellationToken);
            RunChecked(isccPath, ["/Q", issPath]);
            if (!File.Exists(setupOutputPath))
            {
                throw new InvalidOperationException($"Inno Setup did not produce expected installer '{setupOutputPath}'.");
            }

            return;
        }

        await StepAsync("ISCC.exe not found; building self-extracting user-space IntoChat-Setup-win-x64.exe fallback...");
        var sfxDir = Path.Combine(stageRoot, "sfx-setup");
        var sfxOut = Path.Combine(stageRoot, "sfx-out");
        Directory.CreateDirectory(sfxDir);
        Directory.CreateDirectory(sfxOut);

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net11.0</TargetFramework>
                <AssemblyName>IntoChat-Setup-win-x64</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <SelfContained>true</SelfContained>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                <PublishSingleFile>true</PublishSingleFile>
                <IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
                <PublishAot>false</PublishAot>
                <DebugType>none</DebugType>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
              </PropertyGroup>
              <ItemGroup>
                <EmbeddedResource Include="{SecurityElement.Escape(zipPath)}" LogicalName="payload.zip" />
              </ItemGroup>
            </Project>
            """;

        const string programCs = """
            using System.Diagnostics;
            using System.IO.Compression;
            using System.Reflection;

            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "IntoChat OS");
            var desktopExe = Path.Combine(installDir, "IntoChat.OS.Desktop.exe");
            if (File.Exists(desktopExe))
            {
                try
                {
                    using var stop = Process.Start(new ProcessStartInfo(desktopExe, "--quit")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    stop?.WaitForExit(10_000);
                }
                catch
                {
                    // Best-effort stop prior to upgrade.
                }
            }

            var tempExtract = Path.Combine(Path.GetTempPath(), "intochat-setup-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(tempExtract);
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                    ?? throw new InvalidOperationException("Embedded payload.zip not found.");
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(tempExtract, overwriteFiles: true);

                var parent = Path.GetDirectoryName(installDir)!;
                Directory.CreateDirectory(parent);
                var backupDir = installDir + ".prev";
                if (Directory.Exists(backupDir))
                {
                    Directory.Delete(backupDir, recursive: true);
                }
                if (Directory.Exists(installDir))
                {
                    Directory.Move(installDir, backupDir);
                }
                Directory.Move(tempExtract, installDir);
                if (Directory.Exists(backupDir))
                {
                    try { Directory.Delete(backupDir, recursive: true); } catch { }
                }

                var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
                var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (!parts.Any(p => string.Equals(p.TrimEnd('\\'), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                {
                    var updated = string.IsNullOrWhiteSpace(userPath) ? installDir : $"{installDir};{userPath}";
                    Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
                }

                if (!args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase) && File.Exists(desktopExe))
                {
                    Process.Start(new ProcessStartInfo(desktopExe) { WorkingDirectory = installDir, UseShellExecute = true });
                }
            }
            finally
            {
                if (Directory.Exists(tempExtract))
                {
                    try { Directory.Delete(tempExtract, recursive: true); } catch { }
                }
            }
            """;

        var sfxProjPath = Path.Combine(sfxDir, "IntoChatSetup.csproj");
        await File.WriteAllTextAsync(sfxProjPath, csproj, Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sfxDir, "Program.cs"), programCs, Utf8NoBom, cancellationToken);

        RunChecked("dotnet", ["publish", sfxProjPath, "-c", "Release", "-o", sfxOut, "--nologo"]);
        var builtExe = Path.Combine(sfxOut, "IntoChat-Setup-win-x64.exe");
        if (!File.Exists(builtExe))
        {
            throw new InvalidOperationException($"Fallback Windows setup build did not produce '{builtExe}'.");
        }

        File.Copy(builtExe, setupOutputPath, overwrite: true);
    }

    private static string? FindInnoSetupCompiler()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var onPath = ResolveOnPath("ISCC.exe");
        if (onPath is not null)
        {
            return onPath;
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task WriteWindowsReleaseManifestsAsync(
        string repoRoot,
        string outputDir,
        string version,
        string channel,
        string publicDomain,
        string versionedZipName,
        string zipSha256,
        string? versionedMsixName,
        string? msixSha256,
        string? appInstallerFileName,
        string appInstallerBaseUri,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(outputDir, "VERSION"), $"{version}\n", Utf8NoBom, cancellationToken);

        var winX64Asset = new JsonObject
        {
            ["url"] = $"https://releases.{publicDomain}/{channel}/{version}/{versionedZipName}",
            ["sha256"] = zipSha256,
        };
        if (!string.IsNullOrWhiteSpace(versionedMsixName) && !string.IsNullOrWhiteSpace(msixSha256))
        {
            var trimmedBase = appInstallerBaseUri.TrimEnd('/');
            winX64Asset["msixUrl"] = $"{trimmedBase}/{versionedMsixName}";
            winX64Asset["msixSha256"] = msixSha256;
            if (!string.IsNullOrWhiteSpace(appInstallerFileName))
            {
                winX64Asset["appInstallerUrl"] = $"{trimmedBase}/{appInstallerFileName}";
            }
        }

        var latestJsonPath = Path.Combine(outputDir, "latest.json");
        var manifestRoot = new JsonObject
        {
            ["version"] = version,
            ["channel"] = channel,
            ["assets"] = new JsonObject
            {
                ["win-x64"] = winX64Asset,
            },
        };
        var jsonText = manifestRoot.ToJsonString(IndentedJson).Replace("\r\n", "\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(latestJsonPath, jsonText + "\n", Utf8NoBom, cancellationToken);

        var wingetTemplate = Path.Combine(repoRoot, "eng", "release", "winget", "IntoChat.OS.yaml");
        if (File.Exists(wingetTemplate))
        {
            var rendered = await File.ReadAllTextAsync(wingetTemplate, cancellationToken);
            rendered = rendered
                .Replace("PENDING_RELEASE_VERSION", version, StringComparison.Ordinal)
                .Replace("PENDING_RELEASE_CHANNEL", channel, StringComparison.Ordinal)
                .Replace("PENDING_RELEASE_SHA256_WIN_X64", zipSha256, StringComparison.Ordinal);
            if (!string.Equals(publicDomain, PackageOptions.DefaultPublicDomain, StringComparison.Ordinal))
            {
                rendered = rendered.Replace(PackageOptions.DefaultPublicDomain, publicDomain, StringComparison.Ordinal);
            }

            rendered = rendered.Replace("\r\n", "\n", StringComparison.Ordinal);
            var wingetOutDir = Path.Combine(outputDir, "winget");
            Directory.CreateDirectory(wingetOutDir);
            await File.WriteAllTextAsync(Path.Combine(wingetOutDir, "IntoChat.OS.yaml"), rendered, Utf8NoBom, cancellationToken);
        }
    }

    private static async Task PackageLinuxAsync(
        string repoRoot,
        string stageRoot,
        string outputDir,
        string version,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var bundleDir = await StageDesktopBundleAsync(repoRoot, stageRoot, "linux-x64", options, cancellationToken);

        var versionedTar = $"intochat-{version}-linux-x64.tar.gz";
        var stableTar = "intochat-linux-x64.tar.gz";
        var versionedTarPath = Path.Combine(outputDir, versionedTar);
        var stableTarPath = Path.Combine(outputDir, stableTar);

        await StepAsync($"Creating {versionedTar} and {stableTar}...");
        await CreateTarGzAsync(bundleDir, versionedTarPath, cancellationToken);
        File.Copy(versionedTarPath, stableTarPath, overwrite: true);

        var checksums = new List<(string FileName, string Sha256)>
        {
            (versionedTar, ComputeValidatedSha256(versionedTarPath)),
            (stableTar, ComputeValidatedSha256(stableTarPath)),
        };

        var dpkgDeb = ResolveOnPath("dpkg-deb");
        if (dpkgDeb is not null)
        {
            var versionedDeb = $"intochat-{version}-linux-amd64.deb";
            var stableDeb = "intochat-linux-amd64.deb";
            var versionedDebPath = Path.Combine(outputDir, versionedDeb);
            var stableDebPath = Path.Combine(outputDir, stableDeb);

            await BuildDebianPackageAsync(repoRoot, stageRoot, bundleDir, version, dpkgDeb, versionedDebPath, cancellationToken);
            File.Copy(versionedDebPath, stableDebPath, overwrite: true);

            checksums.Add((versionedDeb, ComputeValidatedSha256(versionedDebPath)));
            checksums.Add((stableDeb, ComputeValidatedSha256(stableDebPath)));
        }
        else if (OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("dpkg-deb is required on Linux to generate intochat-linux-amd64.deb.");
        }
        else
        {
            await StepAsync("Skipping .deb generation (dpkg-deb is not available on this host OS).");
        }

        await WriteSha256SumsFileAsync(Path.Combine(outputDir, "SHA256SUMS.linux-x64"), checksums, cancellationToken);
        await StepAsync($"Linux packaging complete in {outputDir}.");
    }

    private static async Task BuildDebianPackageAsync(
        string repoRoot,
        string stageRoot,
        string bundleDir,
        string version,
        string dpkgDebPath,
        string outputDebPath,
        CancellationToken cancellationToken)
    {
        await StepAsync($"Building Debian package {Path.GetFileName(outputDebPath)}...");
        var debRoot = Path.Combine(stageRoot, "deb-root");
        var debianDir = Path.Combine(debRoot, "DEBIAN");
        var optDir = Path.Combine(debRoot, "opt", "intochat");
        var usrBinDir = Path.Combine(debRoot, "usr", "bin");
        var appsDir = Path.Combine(debRoot, "usr", "share", "applications");
        var iconsDir = Path.Combine(debRoot, "usr", "share", "icons", "hicolor", "512x512", "apps");

        Directory.CreateDirectory(debianDir);
        Directory.CreateDirectory(optDir);
        Directory.CreateDirectory(usrBinDir);
        Directory.CreateDirectory(appsDir);

        CopyDirectoryPreservingLinks(bundleDir, optDir);

        var control = $"""
            Package: intochat
            Version: {version}
            Section: utils
            Priority: optional
            Architecture: amd64
            Maintainer: IntoChat <support@intochat.io>
            Homepage: https://intochat.io
            Depends: libgtk-3-0t64 | libgtk-3-0, liblzma5
            Description: IntoChat OS Desktop
             Local-first AI operating system with Ino, live C# micro-apps,
             ArtCraft studios, and hardware-confined sandboxes.
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var controlPath = Path.Combine(debianDir, "control");
        await File.WriteAllTextAsync(controlPath, control, Utf8NoBom, cancellationToken);

        const string binLauncher = "#!/bin/sh\nexec /opt/intochat/IntoChat.OS.Desktop \"$@\"\n";
        var binLauncherPath = Path.Combine(usrBinDir, "intochat");
        await File.WriteAllTextAsync(binLauncherPath, binLauncher, Utf8NoBom, cancellationToken);
        SetExecutableIfPosix(binLauncherPath);

        const string desktopEntry = """
            [Desktop Entry]
            Type=Application
            Name=IntoChat OS
            Comment=Local-first AI operating system
            Exec=/opt/intochat/intochat
            Icon=intochat
            Terminal=false
            Categories=Network;Chat;Utility;Development;
            StartupWMClass=io.intochat.os
            """;
        await File.WriteAllTextAsync(
            Path.Combine(appsDir, "intochat.desktop"),
            desktopEntry.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
            Utf8NoBom,
            cancellationToken);

        var sourceIcon = Path.Combine(repoRoot, "ui", "macos", "Runner", "Assets.xcassets", "AppIcon.appiconset", "app_icon_512.png");
        if (File.Exists(sourceIcon))
        {
            Directory.CreateDirectory(iconsDir);
            File.Copy(sourceIcon, Path.Combine(iconsDir, "intochat.png"), overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(debianDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(controlPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        RunChecked(dpkgDebPath, ["--build", "--root-owner-group", debRoot, outputDebPath]);
    }

    private static async Task PackageMacOsAsync(
        string repoRoot,
        string stageRoot,
        string outputDir,
        string version,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var bundleDir = await StageDesktopBundleAsync(repoRoot, stageRoot, "osx-arm64", options, cancellationToken);

        var versionedTar = $"intochat-{version}-osx-arm64.tar.gz";
        var stableTar = "intochat-osx-arm64.tar.gz";
        var versionedTarPath = Path.Combine(outputDir, versionedTar);
        var stableTarPath = Path.Combine(outputDir, stableTar);

        await StepAsync($"Creating {versionedTar} and {stableTar}...");
        await CreateTarGzAsync(bundleDir, versionedTarPath, cancellationToken);
        File.Copy(versionedTarPath, stableTarPath, overwrite: true);

        var tarSha = ComputeValidatedSha256(versionedTarPath);
        var checksums = new List<(string FileName, string Sha256)>
        {
            (versionedTar, tarSha),
            (stableTar, ComputeValidatedSha256(stableTarPath)),
        };

        var appBundleDir = await CreateMacOsAppBundleAsync(stageRoot, bundleDir, version, cancellationToken);

        var hdiutil = ResolveOnPath("hdiutil");
        if (hdiutil is not null)
        {
            var stableDmg = "IntoChat-macos-arm64.dmg";
            var versionedDmg = $"IntoChat-{version}-macos-arm64.dmg";
            var stableDmgPath = Path.Combine(outputDir, stableDmg);
            var versionedDmgPath = Path.Combine(outputDir, versionedDmg);

            await BuildMacOsDmgAsync(stageRoot, appBundleDir, hdiutil, stableDmgPath);
            File.Copy(stableDmgPath, versionedDmgPath, overwrite: true);

            checksums.Add((versionedDmg, ComputeValidatedSha256(versionedDmgPath)));
            checksums.Add((stableDmg, ComputeValidatedSha256(stableDmgPath)));
        }
        else if (OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException("hdiutil is required on macOS to generate IntoChat-macos-arm64.dmg.");
        }
        else
        {
            await StepAsync("Skipping .dmg generation (hdiutil is only available on macOS).");
        }

        await WriteHomebrewCaskAsync(repoRoot, outputDir, version, options.Channel, options.PublicDomain, tarSha, cancellationToken);
        await WriteSha256SumsFileAsync(Path.Combine(outputDir, "SHA256SUMS.osx-arm64"), checksums, cancellationToken);
        await StepAsync($"macOS packaging complete in {outputDir}.");
    }

    private static async Task<string> CreateMacOsAppBundleAsync(
        string stageRoot,
        string bundleDir,
        string version,
        CancellationToken cancellationToken)
    {
        await StepAsync("Bundling IntoChat.app...");
        var appRoot = Path.Combine(stageRoot, "IntoChat.app");
        var contentsDir = Path.Combine(appRoot, "Contents");
        var macOsDir = Path.Combine(contentsDir, "MacOS");
        var resourcesDir = Path.Combine(contentsDir, "Resources");

        Directory.CreateDirectory(macOsDir);
        Directory.CreateDirectory(resourcesDir);

        CopyDirectoryPreservingLinks(bundleDir, macOsDir);
        SetExecutableIfPosix(Path.Combine(macOsDir, "IntoChat.OS.Desktop"));
        SetExecutableIfPosix(Path.Combine(macOsDir, "intochat"));
        SetExecutableIfPosix(Path.Combine(macOsDir, "os", "IntoChat.OS.Kernel"));
        SetExecutableIfPosix(Path.Combine(macOsDir, "unithost", "IntoChat.OS.Sandbox"));
        SetExecutableIfPosix(Path.Combine(macOsDir, "unithost", "IntoChat.OS.UnitHost"));
        SetExecutableIfPosix(Path.Combine(macOsDir, "shell", "intochat_os_flutter.app", "Contents", "MacOS", "intochat_os_flutter"));

        var flutterIcns = Path.Combine(bundleDir, "shell", "intochat_os_flutter.app", "Contents", "Resources", "AppIcon.icns");
        if (File.Exists(flutterIcns))
        {
            File.Copy(flutterIcns, Path.Combine(resourcesDir, "AppIcon.icns"), overwrite: true);
        }

        var escapedVersion = SecurityElement.Escape(version);
        var infoPlist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleDevelopmentRegion</key>
              <string>en</string>
              <key>CFBundleDisplayName</key>
              <string>IntoChat OS</string>
              <key>CFBundleExecutable</key>
              <string>IntoChat.OS.Desktop</string>
              <key>CFBundleIconFile</key>
              <string>AppIcon</string>
              <key>CFBundleIdentifier</key>
              <string>io.intochat.os</string>
              <key>CFBundleInfoDictionaryVersion</key>
              <string>6.0</string>
              <key>CFBundleName</key>
              <string>IntoChat</string>
              <key>CFBundlePackageType</key>
              <string>APPL</string>
              <key>CFBundleShortVersionString</key>
              <string>{escapedVersion}</string>
              <key>CFBundleVersion</key>
              <string>{escapedVersion}</string>
              <key>LSMinimumSystemVersion</key>
              <string>14.0</string>
              <key>NSHighResolutionCapable</key>
              <true/>
            </dict>
            </plist>
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        await File.WriteAllTextAsync(Path.Combine(contentsDir, "Info.plist"), infoPlist, Utf8NoBom, cancellationToken);
        return appRoot;
    }

    private static async Task BuildMacOsDmgAsync(string stageRoot, string appBundleDir, string hdiutilPath, string dmgOutputPath)
    {
        await StepAsync($"Creating macOS disk image {Path.GetFileName(dmgOutputPath)}...");
        var dmgStage = Path.Combine(stageRoot, "dmg-stage");
        Directory.CreateDirectory(dmgStage);

        var targetApp = Path.Combine(dmgStage, "IntoChat.app");
        CopyDirectoryPreservingLinks(appBundleDir, targetApp);

        var applicationsLink = Path.Combine(dmgStage, "Applications");
        if (!File.Exists(applicationsLink) && !Directory.Exists(applicationsLink))
        {
            Directory.CreateSymbolicLink(applicationsLink, "/Applications");
        }

        if (File.Exists(dmgOutputPath))
        {
            File.Delete(dmgOutputPath);
        }

        RunChecked(hdiutilPath, [
            "create",
            "-volname", "IntoChat OS",
            "-srcfolder", dmgStage,
            "-ov",
            "-format", "UDZO",
            dmgOutputPath,
        ]);
    }

    private static async Task WriteHomebrewCaskAsync(
        string repoRoot,
        string outputDir,
        string version,
        string channel,
        string publicDomain,
        string osxArmSha,
        CancellationToken cancellationToken)
    {
        var brewTemplate = Path.Combine(repoRoot, "eng", "release", "homebrew", "intochat.rb");
        if (!File.Exists(brewTemplate))
        {
            return;
        }

        var rendered = await File.ReadAllTextAsync(brewTemplate, cancellationToken);
        rendered = rendered
            .Replace("PENDING_RELEASE_VERSION", version, StringComparison.Ordinal)
            .Replace("PENDING_RELEASE_CHANNEL", channel, StringComparison.Ordinal)
            .Replace("PENDING_RELEASE_SHA256_OSX_ARM64", osxArmSha, StringComparison.Ordinal);
        if (!string.Equals(publicDomain, PackageOptions.DefaultPublicDomain, StringComparison.Ordinal))
        {
            rendered = rendered.Replace(PackageOptions.DefaultPublicDomain, publicDomain, StringComparison.Ordinal);
        }

        rendered = rendered.Replace("\r\n", "\n", StringComparison.Ordinal);
        var brewOutDir = Path.Combine(outputDir, "homebrew");
        Directory.CreateDirectory(brewOutDir);
        await File.WriteAllTextAsync(Path.Combine(brewOutDir, "intochat.rb"), rendered, Utf8NoBom, cancellationToken);
    }

    private static async Task PackageAndroidAsync(
        string repoRoot,
        string outputDir,
        string version,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var uiDir = Path.Combine(repoRoot, "ui");
        var flutterCmd = ResolveFlutterCommand();

        if (!options.SkipFlutter)
        {
            await StepAsync("Building Android release APK...");
            RunChecked(flutterCmd, ["build", "apk", "--release"], workingDirectory: uiDir);
        }

        var builtApk = Path.Combine(uiDir, "build", "app", "outputs", "flutter-apk", "app-release.apk");
        if (!File.Exists(builtApk))
        {
            throw new InvalidOperationException($"Android release APK not found at '{builtApk}'.");
        }

        var versionedApk = $"intochat-{version}-android.apk";
        var stableApk = "intochat-android.apk";
        var versionedApkPath = Path.Combine(outputDir, versionedApk);
        var stableApkPath = Path.Combine(outputDir, stableApk);

        File.Copy(builtApk, versionedApkPath, overwrite: true);
        File.Copy(builtApk, stableApkPath, overwrite: true);

        var checksums = new List<(string FileName, string Sha256)>
        {
            (versionedApk, ComputeValidatedSha256(versionedApkPath)),
            (stableApk, ComputeValidatedSha256(stableApkPath)),
        };

        await WriteSha256SumsFileAsync(Path.Combine(outputDir, "SHA256SUMS.android"), checksums, cancellationToken);
        await StepAsync($"Android packaging complete in {outputDir}.");
    }

    private static async Task PackageIosAsync(
        string repoRoot,
        string stageRoot,
        string outputDir,
        string version,
        PackageOptions options,
        CancellationToken cancellationToken)
    {
        var uiDir = Path.Combine(repoRoot, "ui");
        var flutterCmd = ResolveFlutterCommand();

        if (!options.SkipFlutter)
        {
            await StepAsync("Building unsigned iOS release app...");
            RunChecked(flutterCmd, ["build", "ios", "--release", "--no-codesign"], workingDirectory: uiDir);
        }

        var runnerApp = Path.Combine(uiDir, "build", "ios", "iphoneos", "Runner.app");
        if (!Directory.Exists(runnerApp))
        {
            throw new InvalidOperationException($"iOS Runner.app not found at '{runnerApp}'.");
        }

        var ipaStage = Path.Combine(stageRoot, "ios-ipa");
        var payloadDir = Path.Combine(ipaStage, "Payload");
        Directory.CreateDirectory(payloadDir);
        CopyDirectoryPreservingLinks(runnerApp, Path.Combine(payloadDir, "Runner.app"));

        var versionedIpa = $"intochat-{version}-ios-unsigned.ipa";
        var stableIpa = "intochat-ios-unsigned.ipa";
        var versionedIpaPath = Path.Combine(outputDir, versionedIpa);
        var stableIpaPath = Path.Combine(outputDir, stableIpa);

        if (File.Exists(versionedIpaPath))
        {
            File.Delete(versionedIpaPath);
        }

        await StepAsync($"Creating {versionedIpa} and {stableIpa}...");
        var zipCmd = ResolveOnPath("zip");
        if (!OperatingSystem.IsWindows() && zipCmd is not null)
        {
            RunChecked(zipCmd, ["-r", "-y", versionedIpaPath, "Payload"], workingDirectory: ipaStage);
        }
        else
        {
            ZipFile.CreateFromDirectory(ipaStage, versionedIpaPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }

        File.Copy(versionedIpaPath, stableIpaPath, overwrite: true);

        var checksums = new List<(string FileName, string Sha256)>
        {
            (versionedIpa, ComputeValidatedSha256(versionedIpaPath)),
            (stableIpa, ComputeValidatedSha256(stableIpaPath)),
        };

        await WriteSha256SumsFileAsync(Path.Combine(outputDir, "SHA256SUMS.ios"), checksums, cancellationToken);
        await StepAsync($"iOS packaging complete in {outputDir}.");
    }

    private static async Task CreateTarGzAsync(string sourceDir, string targetArchivePath, CancellationToken cancellationToken)
    {
        if (File.Exists(targetArchivePath))
        {
            File.Delete(targetArchivePath);
        }

        var tarCmd = ResolveOnPath("tar");
        if (!OperatingSystem.IsWindows() && tarCmd is not null)
        {
            RunChecked(tarCmd, ["-czf", targetArchivePath, "-C", sourceDir, "."]);
            return;
        }

        await using var fileStream = File.Create(targetArchivePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
        await TarFile.CreateFromDirectoryAsync(sourceDir, gzipStream, includeBaseDirectory: false, cancellationToken);
    }

    private static async Task WriteSha256SumsFileAsync(
        string path,
        IReadOnlyList<(string FileName, string Sha256)> entries,
        CancellationToken cancellationToken)
    {
        var lines = entries
            .OrderBy(e => e.FileName, StringComparer.Ordinal)
            .Select(e => $"{e.Sha256}  {e.FileName}");
        await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n", Utf8NoBom, cancellationToken);
    }

    private static string ComputeValidatedSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (hash.Length != 64 || hash.All(c => c == '0'))
        {
            throw new InvalidOperationException($"Computed invalid SHA-256 digest '{hash}' for '{filePath}'.");
        }

        return hash;
    }

    private static void CopyDirectoryPreservingLinks(string sourceDir, string targetDir)
    {
        var cpCmd = ResolveOnPath("cp");
        if (!OperatingSystem.IsWindows() && cpCmd is not null)
        {
            Directory.CreateDirectory(targetDir);
            RunChecked(cpCmd, ["-a", Path.Combine(sourceDir, "."), targetDir]);
            return;
        }

        CopyDirectory(sourceDir, targetDir);
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            var info = new FileInfo(file);
            if (info.LinkTarget is { Length: > 0 } linkTarget)
            {
                if (File.Exists(dest))
                {
                    File.Delete(dest);
                }

                File.CreateSymbolicLink(dest, linkTarget);
            }
            else
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(sub));
            var info = new DirectoryInfo(sub);
            if (info.LinkTarget is { Length: > 0 } linkTarget)
            {
                if (Directory.Exists(dest))
                {
                    Directory.Delete(dest, recursive: true);
                }

                Directory.CreateSymbolicLink(dest, linkTarget);
            }
            else
            {
                CopyDirectory(sub, dest);
            }
        }
    }

    private static void SetExecutableIfPosix(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static string ResolveFlutterCommand()
    {
        var executable = OperatingSystem.IsWindows() ? "flutter.bat" : "flutter";
        return ResolveOnPath(executable) ?? executable;
    }

    private static string? ResolveWinAppCommand()
    {
        var executable = OperatingSystem.IsWindows() ? "winapp.exe" : "winapp";
        var onPath = ResolveOnPath(executable);
        if (onPath is not null)
        {
            return onPath;
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "winapp.exe");
                if (File.Exists(wingetLink))
                {
                    return Path.GetFullPath(wingetLink);
                }
            }
        }

        return null;
    }

    private static string? ResolveOnPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            var direct = Path.Combine(dir, executable);
            if (File.Exists(direct))
            {
                return Path.GetFullPath(direct);
            }

            foreach (var ext in extensions)
            {
                var candidate = direct + ext;
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static void RunChecked(string fileName, string[] args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{fileName} {FormatArgsForError(args)}' failed with exit code {process.ExitCode}.");
        }
    }

    private static string FormatArgsForError(string[] args)
    {
        var formatted = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            formatted.Add(current);
            if ((current.Equals("--cert-password", StringComparison.OrdinalIgnoreCase)
                || current.Equals("--password", StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Length)
            {
                formatted.Add("***");
                i++;
            }
        }

        return string.Join(" ", formatted);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(ex.Message);
        }
    }

    private static Task StepAsync(string message)
        => Console.Out.WriteLineAsync($"  [package] {message}");

    private static string FindRepoRoot()
    {
        var entryDir = AppContext.GetData("EntryPointFileDirectoryPath") as string;
        foreach (var start in new[] { entryDir, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not locate Intochat.OS.slnx in parent directories.");
    }
}
