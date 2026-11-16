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
    public void AllEngScriptsAndManifestsUseIntoChatAiDomainWithoutLegacyIntoChatIo()
    {
        var engRoot = EngPath();
        var files = Directory.EnumerateFiles(engRoot, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(FindRepoRoot(), file);
            Assert.DoesNotContain("intochat.io", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("intochat.ai", text, StringComparison.Ordinal);
            Assert.DoesNotContain(ZeroSha256, text, StringComparison.Ordinal);
            Assert.True(text.Length > 0, relative);
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
        Assert.Contains("https://releases.intochat.ai/", brew, StringComparison.Ordinal);
        Assert.Contains("https://intochat.ai", brew, StringComparison.Ordinal);

        Assert.Contains("PENDING_RELEASE_VERSION", winget, StringComparison.Ordinal);
        Assert.Contains("PENDING_RELEASE_SHA256_WIN_X64", winget, StringComparison.Ordinal);
        Assert.Contains("PENDING_RELEASE_SHA256_WIN_ARM64", winget, StringComparison.Ordinal);
        Assert.Contains("https://releases.intochat.ai/", winget, StringComparison.Ordinal);
        Assert.Contains("https://intochat.ai", winget, StringComparison.Ordinal);
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
                                    url    = "https://releases.intochat.ai/stable/0.17.1/intochat-0.17.1-{{rid}}.zip"
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
                & {{PsEscape(scriptPath)}} -HomeDir {{PsEscape(homeDir)}} {{versionArg}} -NoLaunch
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
    public void PackageReleaseScriptsDryRunUseIntoChatAiAndPopulateManifestsWithoutZeros()
    {
        var version = File.ReadAllText(Path.Combine(FindRepoRoot(), "VERSION")).Trim();
        var temp = Directory.CreateTempSubdirectory("intochat-pkg-test-");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dryRun = RunProcess(
                    ResolvePwsh(),
                    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", EngPath("release", "package-release.ps1"), "-DryRun"],
                    new Dictionary<string, string?>());
                Assert.True(dryRun.ExitCode == 0, $"DryRun failed: {dryRun.StderrOrStdout()}");
                Assert.Contains("intochat.ai", dryRun.StdOut, StringComparison.Ordinal);
                Assert.DoesNotContain("intochat.io", dryRun.StdOut, StringComparison.OrdinalIgnoreCase);

                var outWin = Path.Combine(temp.FullName, "rel-win");
                Directory.CreateDirectory(outWin);
                var zipX64 = CreateDummyZipArchive(outWin, $"intochat-{version}-win-x64.zip", "IntoChat.OS.Desktop.exe", "x64");
                var zipArm = CreateDummyZipArchive(outWin, $"intochat-{version}-win-arm64.zip", "IntoChat.OS.Desktop.exe", "arm64");
                var shaX64 = ComputeSha256Hex(zipX64);
                var shaArm = ComputeSha256Hex(zipArm);

                var renderWin = RunProcess(
                    ResolvePwsh(),
                    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", EngPath("release", "package-release.ps1"), "-OutputDir", outWin, "-UpdateManifests"],
                    new Dictionary<string, string?>());
                Assert.True(renderWin.ExitCode == 0, $"UpdateManifests failed: {renderWin.StderrOrStdout()}");

                var wingetOut = File.ReadAllText(Path.Combine(outWin, "winget", "IntoChat.OS.yaml"));
                Assert.Contains($"PackageVersion: {version}", wingetOut, StringComparison.Ordinal);
                Assert.Contains($"https://releases.intochat.ai/stable/{version}/intochat-{version}-win-x64.zip", wingetOut, StringComparison.Ordinal);
                Assert.Contains($"InstallerSha256: {shaX64}", wingetOut, StringComparison.Ordinal);
                Assert.Contains($"InstallerSha256: {shaArm}", wingetOut, StringComparison.Ordinal);
                Assert.DoesNotContain("PENDING_RELEASE_", wingetOut, StringComparison.Ordinal);
                Assert.DoesNotContain(ZeroSha256, wingetOut, StringComparison.Ordinal);
            }

            if (TryResolvePosixShell(out var viaWsl))
            {
                var pkgShPosix = ToPosixPath(EngPath("release", "package-release.sh"), viaWsl);
                string[] dryArgs = viaWsl
                    ? ["-e", "sh", pkgShPosix, "--dry-run"]
                    : [pkgShPosix, "--dry-run"];
                var exe = viaWsl ? "wsl.exe" : "sh";
                var drySh = RunProcess(exe, dryArgs, new Dictionary<string, string?>());
                Assert.True(drySh.ExitCode == 0, $"package-release.sh --dry-run failed: {drySh.StderrOrStdout()}");
                Assert.Contains("intochat.ai", drySh.StdOut, StringComparison.Ordinal);
                Assert.DoesNotContain("intochat.io", drySh.StdOut, StringComparison.OrdinalIgnoreCase);

                var outOsx = Path.Combine(temp.FullName, "rel-osx");
                Directory.CreateDirectory(outOsx);
                var tarArm = CreateDummyTarGzArchive(outOsx, $"intochat-{version}-osx-arm64.tar.gz", "IntoChat.OS.Desktop", "arm64");
                var tarX64 = CreateDummyTarGzArchive(outOsx, $"intochat-{version}-osx-x64.tar.gz", "IntoChat.OS.Desktop", "x64");
                var shaOsxArm = ComputeSha256Hex(tarArm);
                var shaOsxX64 = ComputeSha256Hex(tarX64);

                var outOsxPosix = ToPosixPath(outOsx, viaWsl);
                string[] renderArgs = viaWsl
                    ? ["-e", "sh", pkgShPosix, "--output", outOsxPosix, "--update-manifests"]
                    : [pkgShPosix, "--output", outOsxPosix, "--update-manifests"];
                var renderSh = RunProcess(exe, renderArgs, new Dictionary<string, string?>());
                Assert.True(renderSh.ExitCode == 0, $"package-release.sh --update-manifests failed: {renderSh.StderrOrStdout()}");

                var brewOut = File.ReadAllText(Path.Combine(outOsx, "homebrew", "intochat.rb"));
                Assert.Contains($"version \"{version}\"", brewOut, StringComparison.Ordinal);
                Assert.Contains($"\"{shaOsxArm}\"", brewOut, StringComparison.Ordinal);
                Assert.Contains($"\"{shaOsxX64}\"", brewOut, StringComparison.Ordinal);
                Assert.Contains("https://releases.intochat.ai/stable/", brewOut, StringComparison.Ordinal);
                Assert.DoesNotContain("PENDING_RELEASE_", brewOut, StringComparison.Ordinal);
                Assert.DoesNotContain(ZeroSha256, brewOut, StringComparison.Ordinal);
            }
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
