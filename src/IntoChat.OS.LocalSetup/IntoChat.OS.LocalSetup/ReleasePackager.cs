using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IntoChat.OS.LocalSetup;

public sealed record ReleasePackageOptions
{
    public string? RepoRoot { get; init; }
    public string? Rid { get; init; }
    public string Configuration { get; init; } = "Release";
    public string Channel { get; init; } = "stable";
    public string PublicDomain { get; init; } = Environment.GetEnvironmentVariable("INTOCHAT_PUBLIC_DOMAIN") is { Length: > 0 } envDomain ? envDomain : DefaultPublicDomain;
    public string? OutputDir { get; init; }
    public bool SkipFlutter { get; init; }
    public bool UpdateManifests { get; init; }
    public bool DryRun { get; init; }
    public bool NativeAotKernel { get; init; } = true;

    public const string DefaultPublicDomain = "intochat.io";

    public static ReleasePackageOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ReleasePackageOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--rid" or "-rid" when i + 1 < args.Length:
                    options = options with { Rid = args[++i] };
                    break;
                case "--configuration" or "-configuration" when i + 1 < args.Length:
                    options = options with { Configuration = args[++i] };
                    break;
                case "--channel" or "-channel" when i + 1 < args.Length:
                    options = options with { Channel = args[++i] };
                    break;
                case "--domain" or "--public-domain" or "-publicdomain" when i + 1 < args.Length:
                    options = options with { PublicDomain = args[++i] };
                    break;
                case "--output" or "--output-dir" or "-outputdir" or "-o" when i + 1 < args.Length:
                    options = options with { OutputDir = args[++i] };
                    break;
                case "--repo-root" when i + 1 < args.Length:
                    options = options with { RepoRoot = args[++i] };
                    break;
                case "--skip-flutter" or "-skipflutter":
                    options = options with { SkipFlutter = true };
                    break;
                case "--update-manifests" or "-updatemanifests":
                    options = options with { UpdateManifests = true };
                    break;
                case "--dry-run" or "-dryrun":
                    options = options with { DryRun = true };
                    break;
                case "--no-aot-kernel":
                    options = options with { NativeAotKernel = false };
                    break;
            }
        }
        return options;
    }
}

public static class ReleasePackager
{
    private static readonly string[] AllSupportedRids = ["win-x64", "win-arm64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64"];

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null, CancellationToken cancellationToken = default)
        => await RunAsync(ReleasePackageOptions.Parse(args), output, error, cancellationToken);

    public static async Task<int> RunAsync(ReleasePackageOptions options, TextWriter? output = null, TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;

        try
        {
            var repoRoot = string.IsNullOrWhiteSpace(options.RepoRoot) ? FindRepoRoot() : Path.GetFullPath(options.RepoRoot);
            var versionFile = Path.Combine(repoRoot, "VERSION");
            if (!File.Exists(versionFile))
            {
                throw new InvalidOperationException($"VERSION file not found at '{versionFile}'.");
            }
            var version = (await File.ReadAllTextAsync(versionFile, cancellationToken)).Trim();
            var rid = string.IsNullOrWhiteSpace(options.Rid) ? ResolveDefaultRid() : options.Rid.Trim();
            var isWindowsTarget = rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase);
            var outputDir = string.IsNullOrWhiteSpace(options.OutputDir)
                ? Path.Combine(repoRoot, "artifacts", "release")
                : Path.GetFullPath(options.OutputDir);

            var archiveExt = isWindowsTarget ? ".zip" : ".tar.gz";
            var archiveName = $"intochat-{version}-{rid}{archiveExt}";
            var archivePath = Path.Combine(outputDir, archiveName);

            await outWriter.WriteLineAsync($"IntoChat OS Release Bundler v{version} ({rid})".AsMemory(), cancellationToken);

            if (options.DryRun)
            {
                foreach (var found in AssertReleaseInputs(repoRoot, isWindowsTarget, options.SkipFlutter))
                {
                    await StepAsync(outWriter, $"[DryRun] Found {found}");
                }
                var kernelMode = options.NativeAotKernel ? "Native AOT" : "R2R";
                await StepAsync(outWriter, $"[DryRun] Would publish IntoChat.OS.Desktop (Native AOT), IntoChat.OS.Kernel ({kernelMode}), IntoChat.OS.Sandbox (UnitHost), and Flutter shell into {archivePath}");
                await StepAsync(outWriter, $"[DryRun] Would emit SHA256SUMS, VERSION, latest.json, winget/IntoChat.OS.yaml, and homebrew/intochat.rb in {outputDir} (domain: {options.PublicDomain})");
                return 0;
            }

            if (options.UpdateManifests)
            {
                if (!Directory.Exists(outputDir))
                {
                    throw new InvalidOperationException($"Output directory '{outputDir}' does not exist.");
                }
                var discovered = DiscoverArchives(outputDir, version);
                if (discovered.Count == 0)
                {
                    throw new InvalidOperationException($"No packaged archives matching 'intochat-{version}-*' found in '{outputDir}'.");
                }
                await PublishReleaseManifestsAsync(repoRoot, outputDir, version, options.Channel, options.PublicDomain, discovered, cancellationToken);
                await StepAsync(outWriter, $"Updated SHA256SUMS, latest.json, winget/IntoChat.OS.yaml, and homebrew/intochat.rb in {outputDir}.");
                return 0;
            }

            _ = AssertReleaseInputs(repoRoot, isWindowsTarget, options.SkipFlutter);
            Directory.CreateDirectory(outputDir);

            var stageRoot = Path.Combine(Path.GetTempPath(), "intochat-pkg-" + Guid.NewGuid().ToString("N")[..12]);
            var bundleDir = Path.Combine(stageRoot, "bundle");
            var osDir = Path.Combine(bundleDir, "os");
            var unitHostDir = Path.Combine(bundleDir, "unithost");
            var shellDir = Path.Combine(bundleDir, "shell");

            Directory.CreateDirectory(bundleDir);
            Directory.CreateDirectory(osDir);
            Directory.CreateDirectory(unitHostDir);
            Directory.CreateDirectory(shellDir);

            try
            {
                var desktopProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Desktop", "IntoChat.OS.Desktop.csproj");
                var kernelProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.csproj");
                var sandboxProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Sandbox", "IntoChat.OS.Sandbox.csproj");
                var uiDir = Path.Combine(repoRoot, "ui");

                await StepAsync(outWriter, $"Publishing IntoChat.OS.Desktop (Native AOT, {rid})...");
                RunChecked("dotnet", ["publish", desktopProj, "-c", options.Configuration, "-r", rid, "-o", bundleDir, "-p:PublishAot=true", "-p:DebugType=none", "--nologo"]);

                if (isWindowsTarget)
                {
                    const string cliShim = "@echo off\r\n\"%~dp0IntoChat.OS.Desktop.exe\" %*\r\n";
                    await File.WriteAllTextAsync(Path.Combine(bundleDir, "intochat.cmd"), cliShim, Encoding.ASCII, cancellationToken);
                }
                else
                {
                    const string posixShim = "#!/bin/sh\nSCRIPT_DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\nexec \"$SCRIPT_DIR/IntoChat.OS.Desktop\" \"$@\"\n";
                    var shimPath = Path.Combine(bundleDir, "intochat");
                    await File.WriteAllTextAsync(shimPath, posixShim, new UTF8Encoding(false), cancellationToken);
                    SetExecutableIfPosix(Path.Combine(bundleDir, "IntoChat.OS.Desktop"));
                    SetExecutableIfPosix(shimPath);
                }

                if (options.NativeAotKernel)
                {
                    await StepAsync(outWriter, $"Publishing IntoChat.OS.Kernel (Native AOT, {rid})...");
                    RunChecked("dotnet", ["publish", kernelProj, "-c", options.Configuration, "-r", rid, "--self-contained", "-o", osDir, "-p:IntoChatOSAot=true", "-p:CodeGraphRefresh=false", "-p:DebugType=none", "--nologo"]);
                }
                else
                {
                    await StepAsync(outWriter, $"Publishing IntoChat.OS.Kernel (Self-Contained ReadyToRun, {rid})...");
                    RunChecked("dotnet", ["publish", kernelProj, "-c", options.Configuration, "-r", rid, "--self-contained", "-o", osDir, "-p:PublishReadyToRun=true", "-p:CodeGraphRefresh=false", "-p:DebugType=none", "--nologo"]);
                }

                if (!isWindowsTarget)
                {
                    SetExecutableIfPosix(Path.Combine(osDir, "IntoChat.OS.Kernel"));
                }

                await StepAsync(outWriter, $"Publishing IntoChat.OS.Sandbox / UnitHost ({rid})...");
                RunChecked("dotnet", ["publish", sandboxProj, "-c", options.Configuration, "-r", rid, "--self-contained", "-o", unitHostDir, "-p:PublishReadyToRun=true", "-p:DebugType=none", "--nologo"]);

                if (isWindowsTarget)
                {
                    const string unitHostShim = "@echo off\r\n\"%~dp0IntoChat.OS.Sandbox.exe\" --unit-host %*\r\n";
                    await File.WriteAllTextAsync(Path.Combine(unitHostDir, "IntoChat.OS.UnitHost.cmd"), unitHostShim, Encoding.ASCII, cancellationToken);
                }
                else
                {
                    const string unitHostPosixShim = "#!/bin/sh\nSCRIPT_DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\nexec \"$SCRIPT_DIR/IntoChat.OS.Sandbox\" --unit-host \"$@\"\n";
                    var unitHostShimPath = Path.Combine(unitHostDir, "IntoChat.OS.UnitHost");
                    await File.WriteAllTextAsync(unitHostShimPath, unitHostPosixShim, new UTF8Encoding(false), cancellationToken);
                    SetExecutableIfPosix(Path.Combine(unitHostDir, "IntoChat.OS.Sandbox"));
                    SetExecutableIfPosix(unitHostShimPath);
                }

                if (!options.SkipFlutter)
                {
                    await BuildFlutterShellAsync(uiDir, shellDir, rid, outWriter);
                }

                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }

                await StepAsync(outWriter, $"Compressing {archiveName}...");
                if (isWindowsTarget)
                {
                    ZipFile.CreateFromDirectory(bundleDir, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
                }
                else
                {
                    await using var fileStream = File.Create(archivePath);
                    await using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                    await TarFile.CreateFromDirectoryAsync(bundleDir, gzipStream, includeBaseDirectory: false, cancellationToken);
                }

                var sha256 = ComputeValidatedSha256(archivePath);
                await StepAsync(outWriter, $"SHA-256: {sha256}");

                var discovered = DiscoverArchives(outputDir, version);
                discovered[rid] = (archiveName, sha256);

                await PublishReleaseManifestsAsync(repoRoot, outputDir, version, options.Channel, options.PublicDomain, discovered, cancellationToken);
                await StepAsync(outWriter, $"Packaged {archivePath} alongside SHA256SUMS, latest.json, and package manager manifests.");
                return 0;
            }
            finally
            {
                if (Directory.Exists(stageRoot))
                {
                    try { Directory.Delete(stageRoot, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning(ex.Message); }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.Message);
            await errWriter.WriteLineAsync($"release: {ex.Message}".AsMemory(), cancellationToken);
            return 1;
        }
    }

    public static string[] AssertReleaseInputs(string repoRoot, bool isWindowsTarget = true, bool skipFlutter = false)
    {
        var desktopProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Desktop", "IntoChat.OS.Desktop.csproj");
        var kernelProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.csproj");
        var sandboxProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Sandbox", "IntoChat.OS.Sandbox.csproj");
        var wingetTemplate = Path.Combine(repoRoot, "eng", "release", "winget", "IntoChat.OS.yaml");
        var brewTemplate = Path.Combine(repoRoot, "eng", "release", "homebrew", "intochat.rb");
        var uiDir = Path.Combine(repoRoot, "ui");

        var inputs = new List<string> { desktopProj, kernelProj, sandboxProj };
        if (isWindowsTarget)
        {
            inputs.Add(wingetTemplate);
        }
        else if (File.Exists(brewTemplate))
        {
            inputs.Add(brewTemplate);
        }

        if (!skipFlutter)
        {
            inputs.Add(Path.Combine(uiDir, "pubspec.yaml"));
            if (isWindowsTarget)
            {
                inputs.Add(Path.Combine(uiDir, "windows", "CMakeLists.txt"));
            }
        }

        var missing = inputs.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Release inputs not found: {string.Join(", ", missing)}");
        }
        return [.. inputs];
    }

    public static async Task PublishReleaseManifestsAsync(
        string repoRoot,
        string outputDir,
        string version,
        string channel,
        string publicDomain,
        IReadOnlyDictionary<string, (string FileName, string Sha256)> assetsByRid,
        CancellationToken cancellationToken = default)
    {
        if (assetsByRid.Count == 0)
        {
            throw new InvalidOperationException("Cannot emit release manifests: no packaged archives were supplied.");
        }

        var utf8NoBom = new UTF8Encoding(false);
        var outVersionPath = Path.Combine(outputDir, "VERSION");
        var sumsPath = Path.Combine(outputDir, "SHA256SUMS");
        var latestJsonPath = Path.Combine(outputDir, "latest.json");

        await File.WriteAllTextAsync(outVersionPath, $"{version}\n", utf8NoBom, cancellationToken);

        var managedNames = assetsByRid.Values.Select(v => v.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingSums = new List<string>();
        if (File.Exists(sumsPath))
        {
            foreach (var rawLine in await File.ReadAllLinesAsync(sumsPath, cancellationToken))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) { continue; }
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var filePart = parts[1].TrimStart('*');
                    if (managedNames.Contains(filePart)) { continue; }
                }
                existingSums.Add(line);
            }
        }

        foreach (var rid in assetsByRid.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var (fileName, sha) = assetsByRid[rid];
            existingSums.Add($"{sha}  {fileName}");
        }
        await File.WriteAllTextAsync(sumsPath, string.Join("\n", existingSums) + "\n", utf8NoBom, cancellationToken);

        var assetsObj = new JsonObject();
        if (File.Exists(latestJsonPath))
        {
            try
            {
                var prior = JsonNode.Parse(await File.ReadAllTextAsync(latestJsonPath, cancellationToken))?.AsObject();
                if (prior?["assets"] is JsonObject priorAssets)
                {
                    foreach (var prop in priorAssets)
                    {
                        if (prop.Value is not null)
                        {
                            assetsObj[prop.Key] = prop.Value.DeepClone();
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Trace.TraceWarning(ex.Message);
            }
        }

        foreach (var rid in assetsByRid.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var (fileName, sha) = assetsByRid[rid];
            assetsObj[rid] = new JsonObject
            {
                ["url"] = $"https://releases.{publicDomain}/{channel}/{version}/{fileName}",
                ["sha256"] = sha,
            };
        }

        var manifestRoot = new JsonObject
        {
            ["version"] = version,
            ["channel"] = channel,
            ["assets"] = assetsObj,
        };
        var jsonText = manifestRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(latestJsonPath, jsonText + "\n", utf8NoBom, cancellationToken);

        // Render WinGet manifest when win-x64 or win-arm64 is present
        var wingetTemplate = Path.Combine(repoRoot, "eng", "release", "winget", "IntoChat.OS.yaml");
        if (File.Exists(wingetTemplate) && (assetsObj.ContainsKey("win-x64") || assetsObj.ContainsKey("win-arm64")))
        {
            var rendered = await File.ReadAllTextAsync(wingetTemplate, cancellationToken);
            rendered = rendered.Replace("PENDING_RELEASE_VERSION", version, StringComparison.Ordinal)
                               .Replace("PENDING_RELEASE_CHANNEL", channel, StringComparison.Ordinal);
            if (!string.Equals(publicDomain, ReleasePackageOptions.DefaultPublicDomain, StringComparison.Ordinal))
            {
                rendered = rendered.Replace(ReleasePackageOptions.DefaultPublicDomain, publicDomain, StringComparison.Ordinal);
            }
            if (assetsObj["win-x64"]?["sha256"]?.GetValue<string>() is { Length: > 0 } winX64Sha)
            {
                rendered = rendered.Replace("PENDING_RELEASE_SHA256_WIN_X64", winX64Sha, StringComparison.Ordinal);
            }
            if (assetsObj["win-arm64"]?["sha256"]?.GetValue<string>() is { Length: > 0 } winArm64Sha)
            {
                rendered = rendered.Replace("PENDING_RELEASE_SHA256_WIN_ARM64", winArm64Sha, StringComparison.Ordinal);
            }
            rendered = rendered.Replace("\r\n", "\n", StringComparison.Ordinal);
            var wingetOutDir = Path.Combine(outputDir, "winget");
            Directory.CreateDirectory(wingetOutDir);
            await File.WriteAllTextAsync(Path.Combine(wingetOutDir, "IntoChat.OS.yaml"), rendered, utf8NoBom, cancellationToken);
        }

        // Render Homebrew cask when osx-arm64 or osx-x64 is present
        var brewTemplate = Path.Combine(repoRoot, "eng", "release", "homebrew", "intochat.rb");
        if (File.Exists(brewTemplate) && (assetsObj.ContainsKey("osx-arm64") || assetsObj.ContainsKey("osx-x64")))
        {
            var rendered = await File.ReadAllTextAsync(brewTemplate, cancellationToken);
            rendered = rendered.Replace("PENDING_RELEASE_VERSION", version, StringComparison.Ordinal)
                               .Replace("PENDING_RELEASE_CHANNEL", channel, StringComparison.Ordinal);
            if (!string.Equals(publicDomain, ReleasePackageOptions.DefaultPublicDomain, StringComparison.Ordinal))
            {
                rendered = rendered.Replace(ReleasePackageOptions.DefaultPublicDomain, publicDomain, StringComparison.Ordinal);
            }
            if (assetsObj["osx-arm64"]?["sha256"]?.GetValue<string>() is { Length: > 0 } osxArmSha)
            {
                rendered = rendered.Replace("PENDING_RELEASE_SHA256_OSX_ARM64", osxArmSha, StringComparison.Ordinal);
            }
            if (assetsObj["osx-x64"]?["sha256"]?.GetValue<string>() is { Length: > 0 } osxX64Sha)
            {
                rendered = rendered.Replace("PENDING_RELEASE_SHA256_OSX_X64", osxX64Sha, StringComparison.Ordinal);
            }
            rendered = rendered.Replace("\r\n", "\n", StringComparison.Ordinal);
            var brewOutDir = Path.Combine(outputDir, "homebrew");
            Directory.CreateDirectory(brewOutDir);
            await File.WriteAllTextAsync(Path.Combine(brewOutDir, "intochat.rb"), rendered, utf8NoBom, cancellationToken);
        }
    }

    private static Dictionary<string, (string FileName, string Sha256)> DiscoverArchives(string outputDir, string version)
    {
        var discovered = new Dictionary<string, (string FileName, string Sha256)>(StringComparer.Ordinal);
        foreach (var candidateRid in AllSupportedRids)
        {
            var ext = candidateRid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";
            var fileName = $"intochat-{version}-{candidateRid}{ext}";
            var fullPath = Path.Combine(outputDir, fileName);
            if (File.Exists(fullPath))
            {
                discovered[candidateRid] = (fileName, ComputeValidatedSha256(fullPath));
            }
        }
        return discovered;
    }

    public static string ComputeValidatedSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (hash.Length != 64 || hash.All(c => c == '0'))
        {
            throw new InvalidOperationException($"Computed invalid SHA-256 digest '{hash}' for '{filePath}'.");
        }
        return hash;
    }

    public static string ResolveDefaultRid()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        if (OperatingSystem.IsWindows()) { return $"win-{arch}"; }
        if (OperatingSystem.IsMacOS()) { return $"osx-{arch}"; }
        return $"linux-{arch}";
    }

    private static async Task BuildFlutterShellAsync(string uiDir, string shellDir, string rid, TextWriter outWriter)
    {
        if (rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
        {
            await StepAsync(outWriter, "Building Flutter Windows shell...");
            RunChecked("flutter", ["build", "windows", "--release"], workingDirectory: uiDir);
            var archSegment = string.Equals(rid, "win-arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
            var runnerOut = Path.Combine(uiDir, "build", "windows", archSegment, "runner", "Release");
            if (!Directory.Exists(runnerOut))
            {
                throw new InvalidOperationException($"Flutter shell for {rid} not found at '{runnerOut}'.");
            }
            CopyDirectory(runnerOut, shellDir);
        }
        else if (rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
        {
            await StepAsync(outWriter, "Building Flutter macOS shell...");
            RunChecked("flutter", ["build", "macos", "--release"], workingDirectory: uiDir);
            var macOut = Path.Combine(uiDir, "build", "macos", "Build", "Products", "Release");
            if (Directory.Exists(macOut))
            {
                CopyDirectory(macOut, shellDir);
            }
        }
        else
        {
            await StepAsync(outWriter, "Building Flutter Linux shell...");
            RunChecked("flutter", ["build", "linux", "--release"], workingDirectory: uiDir);
            var archSegment = string.Equals(rid, "linux-arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
            var linuxOut = Path.Combine(uiDir, "build", "linux", archSegment, "release", "bundle");
            if (Directory.Exists(linuxOut))
            {
                CopyDirectory(linuxOut, shellDir);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
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
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{fileName} {string.Join(" ", args)}' failed with exit code {proc.ExitCode}.");
        }
    }

    private static Task StepAsync(TextWriter writer, string message)
        => writer.WriteLineAsync($"  [release] {message}");

    public static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Could not locate Intochat.OS.slnx in parent directories.");
    }
}
