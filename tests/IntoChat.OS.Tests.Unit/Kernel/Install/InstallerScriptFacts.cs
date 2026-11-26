using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

public sealed class InstallerScriptFacts
{
    private static readonly string ZeroSha256 = new('0', 64);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate IntoChat.OS repository root.");
    }

    private static string EngPath(params string[] segments)
        => Path.Combine([FindRepoRoot(), "eng", .. segments]);

    private static string ResolvePwsh()
    {
        var candidates = new[] { "pwsh", "pwsh.exe", "powershell.exe" };
        foreach (var candidate in candidates)
        {
            var resolved = ResolveOnPath(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        throw new InvalidOperationException("Neither pwsh nor powershell.exe was found on PATH.");
    }

    private static string? ResolveOnPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var direct = Path.Combine(dir, executable);
            if (File.Exists(direct))
            {
                return direct;
            }
            foreach (var ext in extensions)
            {
                var withExt = direct + ext;
                if (File.Exists(withExt))
                {
                    return withExt;
                }
            }
        }
        return null;
    }

    private static bool TryResolvePosixShell(out bool viaWsl)
    {
        if (!OperatingSystem.IsWindows())
        {
            viaWsl = false;
            return File.Exists("/bin/sh") || ResolveOnPath("sh") is not null;
        }

        if (ResolveOnPath("wsl.exe") is null)
        {
            viaWsl = false;
            return false;
        }

        var probe = RunProcess("wsl.exe", ["-e", "sh", "-c", "command -v sha256sum tar mktemp >/dev/null 2>&1"], new Dictionary<string, string?>());
        viaWsl = probe.ExitCode == 0;
        return viaWsl;
    }

    private static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath).Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            return $"/mnt/{drive}{full[2..]}";
        }
        return full;
    }

    private static string ToPosixPath(string path, bool viaWsl)
        => viaWsl ? ToWslPath(path) : path;

    private static (int ExitCode, string StdOut, string StdErr) RunPosixShScript(
        bool viaWsl,
        string shellCmd,
        params string[] scriptArgs)
    {
        var args = new List<string>();
        if (viaWsl)
        {
            args.Add("-e");
            args.Add("sh");
        }
        args.Add("-c");
        args.Add(shellCmd);
        args.AddRange(scriptArgs);
        return RunProcess(viaWsl ? "wsl.exe" : "sh", args, new Dictionary<string, string?>());
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["SPIKE_WINDOWS"] = "0";
        foreach (var (key, value) in environment)
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Debug.WriteLine(ex); }
            throw new TimeoutException($"Process '{fileName}' timed out after 60s.");
        }
        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string CreateDummyZipArchive(string directory, string fileName, string entryName, string content)
    {
        var archivePath = Path.Combine(directory, fileName);
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
        return archivePath;
    }

    private static string CreateDummyTarGzArchive(string directory, string fileName, string entryName, string content)
    {
        var archivePath = Path.Combine(directory, fileName);
        using var fileStream = File.Create(archivePath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Compress);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        var payload = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(payload),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        };
        tar.WriteEntry(entry);
        return archivePath;
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string PsEscape(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Fact]
    public void AllEngScriptsAndManifestsUseIntoChatIoDomainWithoutLegacyIntoChatAi()
    {
        var engRoot = EngPath();
        var files = Directory.EnumerateFiles(engRoot, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(FindRepoRoot(), file);
            Assert.DoesNotContain("intochat.ai", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ZeroSha256, text, StringComparison.Ordinal);
            Assert.True(text.Length > 0, relative);

            var ext = Path.GetExtension(file);
            if (!ext.Equals(".props", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".targets", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".proj", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("intochat.io", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ReleaseManifestsCarryTemplatePlaceholdersNotDummyZeros()
    {
        var brew = File.ReadAllText(EngPath("release", "homebrew", "intochat.rb"));
        var winget = File.ReadAllText(EngPath("release", "winget", "IntoChat.OS.yaml"));

        Assert.DoesNotContain(ZeroSha256, brew, StringComparison.Ordinal);
        Assert.DoesNotContain(ZeroSha256, winget, StringComparison.Ordinal);

        Assert.Contains("PENDING_RELEASE_VERSION", brew, StringComparison.Ordinal);
        Assert.Contains("PENDING_RELEASE_SHA256_OSX_ARM64", brew, StringComparison.Ordinal);
        Assert.Contains("PENDING_RELEASE_SHA256_OSX_X64", brew, StringComparison.Ordinal);
        Assert.Contains("https://releases.intochat.io/", brew, StringComparison.Ordinal);
        Assert.Contains("https://intochat.io", brew, StringComparison.Ordinal);

        Assert.Contains("PENDING_RELEASE_VERSION", winget, StringComparison.Ordinal);
        Assert.Contains("PENDING_RELEASE_SHA256_WIN_X64", winget, StringComparison.Ordinal);
        Assert.Contains("PENDING_RELEASE_SHA256_WIN_ARM64", winget, StringComparison.Ordinal);
        Assert.Contains("https://releases.intochat.io/", winget, StringComparison.Ordinal);
        Assert.Contains("https://intochat.io", winget, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-sha256")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void InstallPs1AbortsWhenSha256IsMissingInvalidZeroOrMismatched(string? candidateHash)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("install.ps1 targets Windows");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-ps1-fail-");
        try
        {
            var zipPath = CreateDummyZipArchive(temp.FullName, "bundle.zip", "IntoChat.OS.Desktop.exe", "fake-binary");
            var homeDir = Path.Combine(temp.FullName, "home");

            var args = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", EngPath("install", "install.ps1"),
                "-LocalArchive", zipPath,
                "-HomeDir", homeDir,
                "-NoLaunch",
            };
            if (candidateHash is not null)
            {
                args.Add("-ExpectedSha256");
                args.Add(candidateHash);
            }

            var env = new Dictionary<string, string?>
            {
                ["INTOCHAT_ARCHIVE_PATH"] = null,
                ["INTOCHAT_ARCHIVE_SHA256"] = null,
                ["INTOCHAT_NO_PATH"] = "1",
                ["INTOCHAT_NO_SHORTCUT"] = "1",
            };

            var (exitCode, stdout, stderr) = RunProcess(ResolvePwsh(), args, env);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("SHA-256", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(homeDir, "bin")), $"Failed verification must not stage bin/. stdout={stdout}\nstderr={stderr}");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void InstallPs1SucceedsWhenLocalArchiveSha256Matches()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("install.ps1 targets Windows");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-ps1-ok-");
        try
        {
            var zipPath = CreateDummyZipArchive(temp.FullName, "bundle.zip", "IntoChat.OS.Desktop.exe", "fake-binary-ok");
            var realSha = ComputeSha256Hex(zipPath).ToUpperInvariant();
            var homeDir = Path.Combine(temp.FullName, "home");

            var args = new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", EngPath("install", "install.ps1"),
                "-LocalArchive", zipPath,
                "-ExpectedSha256", $"  {realSha}  ",
                "-HomeDir", homeDir,
                "-NoLaunch",
                "-NoShortcut",
            };

            var (exitCode, stdout, stderr) = RunProcess(
                ResolvePwsh(),
                args,
                new Dictionary<string, string?> { ["INTOCHAT_NO_PATH"] = "1" });
            Assert.True(exitCode == 0, $"Expected exit 0, got {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.True(File.Exists(Path.Combine(homeDir, "bin", "IntoChat.OS.Desktop.exe")));
            Assert.True(File.Exists(Path.Combine(homeDir, "bin", "intochat.cmd")));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void InstallPs1CreatesStartMenuShortcutUnblocksFilesAndUninstallPreservesSecretsAndState()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("install.ps1 targets Windows");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-ps1-lifecycle-");
        try
        {
            var zipPath = CreateDummyZipArchive(temp.FullName, "bundle.zip", "IntoChat.OS.Desktop.exe", "fake-binary-shortcut");
            var realSha = ComputeSha256Hex(zipPath);
            var homeDir = Path.Combine(temp.FullName, "home");
            var fakeAppData = Path.Combine(temp.FullName, "appdata");
            Directory.CreateDirectory(fakeAppData);

            // Seed custody key and user state before install/uninstall to assert the hard preservation invariant.
            var secretsDir = Path.Combine(homeDir, "secrets");
            var stateDir = Path.Combine(homeDir, "state");
            Directory.CreateDirectory(secretsDir);
            Directory.CreateDirectory(stateDir);
            var masterKeyPath = Path.Combine(secretsDir, "master.key");
            var stateFilePath = Path.Combine(stateDir, "brain.db");
            File.WriteAllText(masterKeyPath, "sovereign-custody-key-sentinel");
            File.WriteAllText(stateFilePath, "user-state-data-sentinel");

            var env = new Dictionary<string, string?>
            {
                ["APPDATA"] = fakeAppData,
                ["INTOCHAT_NO_PATH"] = "1",
                ["INTOCHAT_NO_SHORTCUT"] = null,
            };

            string[] installArgs =
            [
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", EngPath("install", "install.ps1"),
                "-LocalArchive", zipPath,
                "-ExpectedSha256", realSha,
                "-HomeDir", homeDir,
                "-NoLaunch",
            ];

            var (installExit, installOut, installErr) = RunProcess(ResolvePwsh(), installArgs, env);
            Assert.True(installExit == 0, $"Install failed (exit {installExit}).\nstdout:\n{installOut}\nstderr:\n{installErr}");

            var expectedBinDir = Path.Combine(homeDir, "bin");
            var expectedDesktopExe = Path.Combine(expectedBinDir, "IntoChat.OS.Desktop.exe");
            var expectedShortcut = Path.Combine(fakeAppData, "Microsoft", "Windows", "Start Menu", "Programs", "IntoChat OS.lnk");
            Assert.True(File.Exists(expectedDesktopExe));
            Assert.True(File.Exists(expectedShortcut), $"Expected Start Menu shortcut at {expectedShortcut}.\nstdout:\n{installOut}\nstderr:\n{installErr}");

            // Inspect created shortcut properties via WScript.Shell COM.
            var inspectCmd = $$"""
                $shell = New-Object -ComObject WScript.Shell
                $sc = $shell.CreateShortcut({{PsEscape(expectedShortcut)}})
                Write-Output ("TARGET=" + $sc.TargetPath)
                Write-Output ("WORKDIR=" + $sc.WorkingDirectory)
                Write-Output ("DESC=" + $sc.Description)
                Write-Output ("ICON=" + $sc.IconLocation)
                """;
            var (inspectExit, inspectOut, inspectErr) = RunProcess(
                ResolvePwsh(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", inspectCmd],
                new Dictionary<string, string?>());
            Assert.True(inspectExit == 0, $"Shortcut inspection failed: {inspectOut}\n{inspectErr}");
            Assert.Contains($"TARGET={expectedDesktopExe}", inspectOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"WORKDIR={expectedBinDir}", inspectOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DESC=IntoChat OS", inspectOut, StringComparison.Ordinal);
            Assert.Contains($"ICON={expectedDesktopExe},0", inspectOut, StringComparison.OrdinalIgnoreCase);

            // Uninstall must remove bin/ and the Start Menu shortcut while strictly preserving secrets and state.
            string[] uninstallArgs =
            [
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", EngPath("install", "install.ps1"),
                "-Uninstall",
                "-HomeDir", homeDir,
                "-NoPath",
            ];
            var (uninstallExit, uninstallOut, uninstallErr) = RunProcess(ResolvePwsh(), uninstallArgs, env);
            Assert.True(uninstallExit == 0, $"Uninstall failed (exit {uninstallExit}).\nstdout:\n{uninstallOut}\nstderr:\n{uninstallErr}");

            Assert.False(Directory.Exists(expectedBinDir), "Uninstall must remove bin/ directory.");
            Assert.False(File.Exists(expectedShortcut), "Uninstall must remove Start Menu shortcut.");
            Assert.True(File.Exists(masterKeyPath), "HARD INVARIANT: Uninstall must NEVER delete ~/.intochat/secrets/master.key.");
            Assert.Equal("sovereign-custody-key-sentinel", File.ReadAllText(masterKeyPath));
            Assert.True(File.Exists(stateFilePath), "HARD INVARIANT: Uninstall must NEVER delete ~/.intochat/state.");
            Assert.Equal("user-state-data-sentinel", File.ReadAllText(stateFilePath));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void InstallPs1NoShortcutSkipsStartMenuShortcutCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("install.ps1 targets Windows");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-ps1-noshortcut-");
        try
        {
            var zipPath = CreateDummyZipArchive(temp.FullName, "bundle.zip", "IntoChat.OS.Desktop.exe", "fake-binary");
            var realSha = ComputeSha256Hex(zipPath);
            var homeDir = Path.Combine(temp.FullName, "home");
            var fakeAppData = Path.Combine(temp.FullName, "appdata");
            Directory.CreateDirectory(fakeAppData);

            var (exitCode, stdout, stderr) = RunProcess(
                ResolvePwsh(),
                [
                    "-NoProfile",
                    "-ExecutionPolicy", "Bypass",
                    "-File", EngPath("install", "install.ps1"),
                    "-LocalArchive", zipPath,
                    "-ExpectedSha256", realSha,
                    "-HomeDir", homeDir,
                    "-NoLaunch",
                    "-NoShortcut",
                ],
                new Dictionary<string, string?>
                {
                    ["APPDATA"] = fakeAppData,
                    ["INTOCHAT_NO_PATH"] = "1",
                });

            Assert.True(exitCode == 0, $"Expected exit 0, got {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var shortcutPath = Path.Combine(fakeAppData, "Microsoft", "Windows", "Start Menu", "Programs", "IntoChat OS.lnk");
            Assert.False(File.Exists(shortcutPath), "Start Menu shortcut must not be created when -NoShortcut is passed.");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InstallPs1RegisterIdentityInvokesWinAppAndWarnsNonFatallyOnFailure(int mockExitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("install.ps1 targets Windows");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-ps1-winapp-");
        try
        {
            var zipPath = CreateDummyZipArchive(temp.FullName, "bundle.zip", "IntoChat.OS.Desktop.exe", "fake-binary");
            var realSha = ComputeSha256Hex(zipPath);
            var homeDir = Path.Combine(temp.FullName, "home");
            var invocationLog = Path.Combine(temp.FullName, "winapp-args.txt");
            var scriptPath = EngPath("install", "install.ps1");

            var command = $$"""
                $ErrorActionPreference = 'Stop'
                function global:winapp {
                    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ArgsRest)
                    Set-Content -LiteralPath {{PsEscape(invocationLog)}} -Value ($ArgsRest -join '|') -Encoding UTF8
                    $global:LASTEXITCODE = {{mockExitCode}}
                }
                & {{PsEscape(scriptPath)}} -LocalArchive {{PsEscape(zipPath)}} -ExpectedSha256 {{PsEscape(realSha)}} -HomeDir {{PsEscape(homeDir)}} -NoLaunch -NoShortcut -NoPath -RegisterIdentity
                """;

            var (exitCode, stdout, stderr) = RunProcess(
                ResolvePwsh(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
                new Dictionary<string, string?>());

            Assert.True(exitCode == 0, $"Install with -RegisterIdentity must remain non-fatal when winapp exits {mockExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.True(File.Exists(invocationLog), "Expected mock winapp to be invoked.");
            var recorded = File.ReadAllText(invocationLog).Trim();
            var expectedExe = Path.Combine(homeDir, "bin", "IntoChat.OS.Desktop.exe");
            Assert.Equal($"create-debug-identity|{expectedExe}", recorded, ignoreCase: true);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("manifest-missing-sha")]
    [InlineData("manifest-zero-sha")]
    [InlineData("manifest-mismatch-sha")]
    [InlineData("sums-missing-entry")]
    [InlineData("sums-valid")]
    public void InstallPs1RemoteManifestAndSumsEnforceMandatorySha256(string scenario)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("install.ps1 targets Windows");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-ps1-remote-");
        try
        {
            var zipPath = CreateDummyZipArchive(temp.FullName, "bundle.zip", "IntoChat.OS.Desktop.exe", "remote-binary");
            var realSha = ComputeSha256Hex(zipPath);
            var homeDir = Path.Combine(temp.FullName, "home");
            var scriptPath = EngPath("install", "install.ps1");
            var rid = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "win-arm64" : "win-x64";

            var versionArg = scenario.StartsWith("sums-", StringComparison.Ordinal) ? "-Version '0.17.1'" : "";
            var manifestShaExpr = scenario switch
            {
                "manifest-missing-sha" => "''",
                "manifest-zero-sha" => PsEscape(ZeroSha256),
                "manifest-mismatch-sha" => PsEscape(new string('a', 64)),
                _ => PsEscape(realSha),
            };
            var sumsExpr = scenario switch
            {
                "sums-missing-entry" => "'deadbeef  other-file.zip'",
                "sums-valid" => PsEscape($"{realSha}  intochat-0.17.1-{rid}.zip"),
                _ => "''",
            };

            var command = $$"""
                $ErrorActionPreference = 'Stop'
                function global:Invoke-RestMethod {
                    param([string]$Uri, [string]$Method, [switch]$UseBasicParsing)
                    if ($Uri -like '*latest.json') {
                        return [pscustomobject]@{
                            version = '0.17.1'
                            assets  = [pscustomobject]@{
                                {{PsEscape(rid)}} = [pscustomobject]@{
                                    url    = "https://releases.intochat.io/stable/0.17.1/intochat-0.17.1-{{rid}}.zip"
                                    sha256 = {{manifestShaExpr}}
                                }
                            }
                        }
                    }
                    if ($Uri -like '*SHA256SUMS') {
                        return {{sumsExpr}}
                    }
                    throw "Unexpected Invoke-RestMethod URI: $Uri"
                }
                function global:Invoke-WebRequest {
                    param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing)
                    Copy-Item -LiteralPath {{PsEscape(zipPath)}} -Destination $OutFile -Force
                }
                & {{PsEscape(scriptPath)}} -HomeDir {{PsEscape(homeDir)}} {{versionArg}} -NoLaunch -NoShortcut
                """;

            var (exitCode, stdout, stderr) = RunProcess(
                ResolvePwsh(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
                new Dictionary<string, string?>
                {
                    ["INTOCHAT_ARCHIVE_PATH"] = null,
                    ["INTOCHAT_ARCHIVE_SHA256"] = null,
                    ["INTOCHAT_NO_PATH"] = "1",
                });

            if (scenario == "sums-valid")
            {
                Assert.True(exitCode == 0, $"Expected exit 0, got {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
                Assert.True(File.Exists(Path.Combine(homeDir, "bin", "IntoChat.OS.Desktop.exe")));
            }
            else
            {
                Assert.NotEqual(0, exitCode);
                Assert.Contains("SHA-256", stderr, StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(Path.Combine(homeDir, "bin")), $"Scenario '{scenario}' should fail closed without staging bin/. stdout={stdout}\nstderr={stderr}");
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-sha256")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void InstallShAbortsWhenSha256IsMissingInvalidZeroOrMismatched(string? candidateHash)
    {
        if (!TryResolvePosixShell(out var viaWsl))
        {
            Assert.Skip("POSIX sh (or WSL on Windows) is not available.");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-sh-fail-");
        try
        {
            var tarGzPath = CreateDummyTarGzArchive(temp.FullName, "bundle.tar.gz", "IntoChat.OS.Desktop", "#!/bin/sh\necho ok\n");
            var fakeUserHome = Path.Combine(temp.FullName, "userhome");
            Directory.CreateDirectory(fakeUserHome);
            var installHome = Path.Combine(temp.FullName, "installhome");

            var scriptPosix = ToPosixPath(EngPath("install", "install.sh"), viaWsl);
            var archivePosix = ToPosixPath(tarGzPath, viaWsl);
            var userHomePosix = ToPosixPath(fakeUserHome, viaWsl);
            var installHomePosix = ToPosixPath(installHome, viaWsl);

            var hashSetup = candidateHash is null
                ? "unset INTOCHAT_ARCHIVE_SHA256"
                : "export INTOCHAT_ARCHIVE_SHA256=\"$5\"";

            var shellCmd = $$"""
                export HOME="$1"
                export INTOCHAT_ARCHIVE_PATH="$2"
                export INTOCHAT_NO_PATH=1
                export SPIKE_WINDOWS=0
                {{hashSetup}}
                exec sh "$3" --home "$4" --no-launch
                """.Replace("\r\n", "\n", StringComparison.Ordinal);

            string[] scriptArgs = candidateHash is null
                ? ["installer-test", userHomePosix, archivePosix, scriptPosix, installHomePosix]
                : ["installer-test", userHomePosix, archivePosix, scriptPosix, installHomePosix, candidateHash];
            var (exitCode, stdout, stderr) = RunPosixShScript(viaWsl, shellCmd, scriptArgs);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("SHA-256", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(installHome, "bin")), $"Failed verification must not stage bin/. stdout={stdout}\nstderr={stderr}");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void InstallShSucceedsWhenLocalArchiveSha256Matches()
    {
        if (!TryResolvePosixShell(out var viaWsl))
        {
            Assert.Skip("POSIX sh (or WSL on Windows) is not available.");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-sh-ok-");
        try
        {
            var tarGzPath = CreateDummyTarGzArchive(temp.FullName, "bundle.tar.gz", "IntoChat.OS.Desktop", "#!/bin/sh\necho ok\n");
            var realSha = ComputeSha256Hex(tarGzPath).ToUpperInvariant();
            var fakeUserHome = Path.Combine(temp.FullName, "userhome");
            Directory.CreateDirectory(fakeUserHome);
            var installHome = Path.Combine(temp.FullName, "installhome");

            var scriptPosix = ToPosixPath(EngPath("install", "install.sh"), viaWsl);
            var archivePosix = ToPosixPath(tarGzPath, viaWsl);
            var userHomePosix = ToPosixPath(fakeUserHome, viaWsl);
            var installHomePosix = ToPosixPath(installHome, viaWsl);

            var shellCmd = """
                export HOME="$1"
                export INTOCHAT_ARCHIVE_PATH="$2"
                export INTOCHAT_ARCHIVE_SHA256="$5"
                export INTOCHAT_NO_PATH=1
                export SPIKE_WINDOWS=0
                exec sh "$3" --home "$4" --no-launch
                """.Replace("\r\n", "\n", StringComparison.Ordinal);

            var (exitCode, stdout, stderr) = RunPosixShScript(
                viaWsl,
                shellCmd,
                "installer-test",
                userHomePosix,
                archivePosix,
                scriptPosix,
                installHomePosix,
                $"  {realSha}  ");
            Assert.True(exitCode == 0, $"Expected exit 0, got {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.True(File.Exists(Path.Combine(installHome, "bin", "IntoChat.OS.Desktop")));
            Assert.True(File.Exists(Path.Combine(installHome, "bin", "intochat")));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("sums-missing-entry")]
    [InlineData("sums-zero-hash")]
    [InlineData("sums-mismatch")]
    [InlineData("sums-valid")]
    public void InstallShRemoteSumsEnforcesMandatorySha256(string scenario)
    {
        if (!TryResolvePosixShell(out var viaWsl))
        {
            Assert.Skip("POSIX sh (or WSL on Windows) is not available.");
        }

        var temp = Directory.CreateTempSubdirectory("intochat-sh-remote-");
        try
        {
            var tarGzPath = CreateDummyTarGzArchive(temp.FullName, "bundle.tar.gz", "IntoChat.OS.Desktop", "#!/bin/sh\necho ok\n");
            var realSha = ComputeSha256Hex(tarGzPath);
            var fakeUserHome = Path.Combine(temp.FullName, "userhome");
            var mockBin = Path.Combine(temp.FullName, "mockbin");
            Directory.CreateDirectory(fakeUserHome);
            Directory.CreateDirectory(mockBin);
            var installHome = Path.Combine(temp.FullName, "installhome");

            var sumsContent = scenario switch
            {
                "sums-missing-entry" => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  intochat-0.17.1-other.tar.gz\n",
                "sums-zero-hash" => $"{ZeroSha256}  intochat-0.17.1-linux-x64.tar.gz\n",
                "sums-mismatch" => $"{new string('b', 64)}  intochat-0.17.1-linux-x64.tar.gz\n",
                "sums-valid" => $"{realSha}  intochat-0.17.1-linux-x64.tar.gz\n",
                _ => "",
            };

            var mockCurl = Path.Combine(mockBin, "curl");
            var tarGzPosix = ToPosixPath(tarGzPath, viaWsl);
            var sumsFile = Path.Combine(temp.FullName, "SHA256SUMS");
            File.WriteAllText(sumsFile, sumsContent, new UTF8Encoding(false));
            var sumsPosix = ToPosixPath(sumsFile, viaWsl);

            var curlBody = $$"""
                #!/bin/sh
                dest=""
                url=""
                while [ "$#" -gt 0 ]; do
                    case "$1" in
                        -o) dest="$2"; shift 2 ;;
                        --proto) shift 2 ;;
                        -fsSL|--tlsv1.2) shift ;;
                        *) url="$1"; shift ;;
                    esac
                done
                case "$url" in
                    */VERSION) printf '0.17.1\n' > "$dest" ;;
                    */SHA256SUMS) cp {{sumsPosix}} "$dest" ;;
                    *.tar.gz) cp {{tarGzPosix}} "$dest" ;;
                    *) echo "Unexpected URL: $url" >&2; exit 1 ;;
                esac
                """;
            File.WriteAllText(mockCurl, curlBody.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

            var scriptPosix = ToPosixPath(EngPath("install", "install.sh"), viaWsl);
            var userHomePosix = ToPosixPath(fakeUserHome, viaWsl);
            var installHomePosix = ToPosixPath(installHome, viaWsl);
            var mockBinPosix = ToPosixPath(mockBin, viaWsl);

            var shellCmd = """
                export HOME="$1"
                export PATH="$2:$PATH"
                export INTOCHAT_NO_PATH=1
                export SPIKE_WINDOWS=0
                unset INTOCHAT_ARCHIVE_PATH
                unset INTOCHAT_ARCHIVE_SHA256
                chmod +x "$2/curl"
                exec sh "$3" --home "$4" --no-launch
                """.Replace("\r\n", "\n", StringComparison.Ordinal);

            var (exitCode, stdout, stderr) = RunPosixShScript(
                viaWsl,
                shellCmd,
                "installer-test",
                userHomePosix,
                mockBinPosix,
                scriptPosix,
                installHomePosix);

            if (scenario == "sums-valid")
            {
                Assert.True(exitCode == 0, $"Expected exit 0, got {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
                Assert.True(File.Exists(Path.Combine(installHome, "bin", "IntoChat.OS.Desktop")));
            }
            else
            {
                Assert.NotEqual(0, exitCode);
                Assert.Contains("SHA-256", stderr, StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(Path.Combine(installHome, "bin")), $"Scenario '{scenario}' should fail closed without staging bin/. stdout={stdout}\nstderr={stderr}");
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PackageReleaseScriptsDryRunUseIntoChatIoAndPopulateManifestsWithoutZeros()
    {
        var repoRoot = FindRepoRoot();
        var version = (await File.ReadAllTextAsync(Path.Combine(repoRoot, "VERSION"), TestContext.Current.CancellationToken)).Trim();
        var temp = Directory.CreateTempSubdirectory("intochat-pkg-test-");
        try
        {
            var dryOut = new StringWriter();
            var dryErr = new StringWriter();
            var dryExit = await IntoChat.OS.LocalSetup.ReleasePackager.RunAsync(new IntoChat.OS.LocalSetup.ReleasePackageOptions
            {
                RepoRoot = repoRoot,
                Rid = "win-x64",
                DryRun = true,
            }, dryOut, dryErr, TestContext.Current.CancellationToken);
            Assert.True(dryExit == 0, $"DryRun failed: {dryOut}\n{dryErr}");
            Assert.Contains("intochat.io", dryOut.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("intochat.ai", dryOut.ToString(), StringComparison.OrdinalIgnoreCase);

            var outWin = Path.Combine(temp.FullName, "rel-win");
            Directory.CreateDirectory(outWin);
            var zipX64 = CreateDummyZipArchive(outWin, $"intochat-{version}-win-x64.zip", "IntoChat.OS.Desktop.exe", "x64");
            var zipArm = CreateDummyZipArchive(outWin, $"intochat-{version}-win-arm64.zip", "IntoChat.OS.Desktop.exe", "arm64");
            var shaX64 = ComputeSha256Hex(zipX64);
            var shaArm = ComputeSha256Hex(zipArm);

            var renderWinOut = new StringWriter();
            var renderWinErr = new StringWriter();
            var renderWinExit = await IntoChat.OS.LocalSetup.ReleasePackager.RunAsync(new IntoChat.OS.LocalSetup.ReleasePackageOptions
            {
                RepoRoot = repoRoot,
                OutputDir = outWin,
                UpdateManifests = true,
            }, renderWinOut, renderWinErr, TestContext.Current.CancellationToken);
            Assert.True(renderWinExit == 0, $"UpdateManifests failed: {renderWinOut}\n{renderWinErr}");

            var wingetOut = await File.ReadAllTextAsync(Path.Combine(outWin, "winget", "IntoChat.OS.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains($"PackageVersion: {version}", wingetOut, StringComparison.Ordinal);
            Assert.Contains($"https://releases.intochat.io/stable/{version}/intochat-{version}-win-x64.zip", wingetOut, StringComparison.Ordinal);
            Assert.Contains($"InstallerSha256: {shaX64}", wingetOut, StringComparison.Ordinal);
            Assert.Contains($"InstallerSha256: {shaArm}", wingetOut, StringComparison.Ordinal);
            Assert.DoesNotContain("PENDING_RELEASE_", wingetOut, StringComparison.Ordinal);
            Assert.DoesNotContain(ZeroSha256, wingetOut, StringComparison.Ordinal);

            var outOsx = Path.Combine(temp.FullName, "rel-osx");
            Directory.CreateDirectory(outOsx);
            var tarArm = CreateDummyTarGzArchive(outOsx, $"intochat-{version}-osx-arm64.tar.gz", "IntoChat.OS.Desktop", "arm64");
            var tarX64 = CreateDummyTarGzArchive(outOsx, $"intochat-{version}-osx-x64.tar.gz", "IntoChat.OS.Desktop", "x64");
            var shaOsxArm = ComputeSha256Hex(tarArm);
            var shaOsxX64 = ComputeSha256Hex(tarX64);

            var renderOsxOut = new StringWriter();
            var renderOsxErr = new StringWriter();
            var renderOsxExit = await IntoChat.OS.LocalSetup.ReleasePackager.RunAsync(new IntoChat.OS.LocalSetup.ReleasePackageOptions
            {
                RepoRoot = repoRoot,
                OutputDir = outOsx,
                UpdateManifests = true,
            }, renderOsxOut, renderOsxErr, TestContext.Current.CancellationToken);
            Assert.True(renderOsxExit == 0, $"UpdateManifests (osx) failed: {renderOsxOut}\n{renderOsxErr}");

            var brewOut = await File.ReadAllTextAsync(Path.Combine(outOsx, "homebrew", "intochat.rb"), TestContext.Current.CancellationToken);
            Assert.Contains($"version \"{version}\"", brewOut, StringComparison.Ordinal);
            Assert.Contains($"\"{shaOsxArm}\"", brewOut, StringComparison.Ordinal);
            Assert.Contains($"\"{shaOsxX64}\"", brewOut, StringComparison.Ordinal);
            Assert.Contains("https://releases.intochat.io/stable/", brewOut, StringComparison.Ordinal);
            Assert.DoesNotContain("PENDING_RELEASE_", brewOut, StringComparison.Ordinal);
            Assert.DoesNotContain(ZeroSha256, brewOut, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}

file static class ProcessResultExtensions
{
    public static string StderrOrStdout(this (int ExitCode, string StdOut, string StdErr) result)
        => $"exit={result.ExitCode}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}";
}
