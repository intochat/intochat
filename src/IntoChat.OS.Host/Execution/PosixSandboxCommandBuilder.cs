// os-only: Linux Bubblewrap and macOS Seatbelt command builders and POSIX confined runner.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Builds deterministic Linux <c>bwrap</c> arguments and macOS <c>sandbox-exec</c> Seatbelt profiles,
/// enforcing zero ambient environment inheritance, read-only <c>.git</c> metadata protection, and network confinement.
/// </summary>
public static class PosixSandboxCommandBuilder
{
    private static readonly string[] SafePosixSystemPaths =
    [
        "/usr",
        "/bin",
        "/sbin",
        "/lib",
        "/lib64",
        "/etc/alternatives",
        "/etc/ssl",
        "/etc/ca-certificates",
    ];

    public static IReadOnlyDictionary<string, string> BuildCleanPosixEnvironment(ConfinementSpec spec, string tempDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);

        var env = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = spec.WorkingDirectory,
            ["TMPDIR"] = tempDirectory,
            ["TEMP"] = tempDirectory,
            ["TMP"] = tempDirectory,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

        foreach (var (key, value) in spec.CleanEnvironment)
        {
            if (!IsBlockedSecretVariableName(key))
            {
                env[key] = value;
            }
        }

        return env;
    }

    public static bool IsBlockedSecretVariableName(string name)
        => ChildEnvironmentPolicy.IsBlockedSecretVariableName(name);

    public static IReadOnlyList<string> BuildBubblewrapArguments(ConfinementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);

        var args = new List<string>
        {
            "--unshare-user",
            "--unshare-pid",
            "--unshare-ipc",
            "--unshare-uts",
            "--die-with-parent",
            "--new-session",
            "--clearenv",
            "--proc", "/proc",
            "--dev", "/dev",
            "--tmpfs", "/tmp",
        };

        if (spec.Network == NetworkPolicy.None)
        {
            args.Add("--unshare-net");
        }
        else
        {
            args.Add("--ro-bind-try");
            args.Add("/etc/resolv.conf");
            args.Add("/etc/resolv.conf");
        }

        foreach (var sysPath in SafePosixSystemPaths)
        {
            args.Add("--ro-bind-try");
            args.Add(sysPath);
            args.Add(sysPath);
        }

        foreach (var ro in spec.ReadOnlyPaths.Distinct(StringComparer.Ordinal))
        {
            args.Add("--ro-bind");
            args.Add(ro);
            args.Add(ro);
        }

        var rwSet = new HashSet<string>(spec.ReadWritePaths, StringComparer.Ordinal)
        {
            spec.WorkingDirectory,
        };

        foreach (var rw in rwSet.OrderBy(static p => p, StringComparer.Ordinal))
        {
            args.Add("--bind");
            args.Add(rw);
            args.Add(rw);

            var gitDir = CombineNormalized(rw, ".git");
            args.Add("--ro-bind-try");
            args.Add(gitDir);
            args.Add(gitDir);
        }

        var cleanEnv = BuildCleanPosixEnvironment(spec, "/tmp");
        foreach (var (key, value) in cleanEnv)
        {
            args.Add("--setenv");
            args.Add(key);
            args.Add(value);
        }

        args.Add("--chdir");
        args.Add(spec.WorkingDirectory);
        args.Add("--");
        args.Add(spec.ExecutablePath);
        args.AddRange(spec.Arguments);

        return args;
    }

    public static string BuildSeatbeltProfile(ConfinementSpec spec, string tempDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);

        var sb = new StringBuilder();
        sb.Append("(version 1)\n");
        sb.Append("(deny default)\n");
        sb.Append("(allow process-exec process-fork signal sysctl-read mach-lookup)\n");
        sb.Append("(allow file-read*\n");
        sb.Append("  (subpath \"/usr\")\n");
        sb.Append("  (subpath \"/bin\")\n");
        sb.Append("  (subpath \"/sbin\")\n");
        sb.Append("  (subpath \"/System/Library\")\n");
        sb.Append("  (subpath \"/Library/Frameworks\")\n");
        sb.Append("  (subpath \"/private/etc\")\n");
        sb.Append("  (subpath \"/dev\")\n");

        var readable = new HashSet<string>(spec.ReadOnlyPaths, StringComparer.Ordinal)
        {
            spec.WorkingDirectory,
            tempDirectory,
        };
        foreach (var rw in spec.ReadWritePaths)
        {
            readable.Add(rw);
        }

        foreach (var path in readable.OrderBy(static p => p, StringComparer.Ordinal))
        {
            sb.Append("  (subpath \"").Append(EscapeSeatbeltString(path)).Append("\")\n");
        }
        sb.Append(")\n");

        sb.Append("(allow file-write*\n");
        sb.Append("  (subpath \"").Append(EscapeSeatbeltString(tempDirectory)).Append("\")\n");
        var writable = new HashSet<string>(spec.ReadWritePaths, StringComparer.Ordinal)
        {
            spec.WorkingDirectory,
        };
        foreach (var path in writable.OrderBy(static p => p, StringComparer.Ordinal))
        {
            sb.Append("  (subpath \"").Append(EscapeSeatbeltString(path)).Append("\")\n");
        }
        sb.Append(")\n");

        foreach (var path in writable.OrderBy(static p => p, StringComparer.Ordinal))
        {
            var gitPath = CombineNormalized(path, ".git");
            sb.Append("(deny file-write* (subpath \"").Append(EscapeSeatbeltString(gitPath)).Append("\"))\n");
        }

        if (spec.Network == NetworkPolicy.PublicInternetOnly)
        {
            sb.Append("(allow network-outbound (remote ip))\n");
            sb.Append("(deny network-outbound (remote ip \"localhost:*\"))\n");
        }
        else
        {
            sb.Append("(deny network*)\n");
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> BuildSeatbeltArguments(ConfinementSpec spec, string tempDirectory)
    {
        var profile = BuildSeatbeltProfile(spec, tempDirectory);
        var args = new List<string>(4 + spec.Arguments.Count)
        {
            "-p",
            profile,
            spec.ExecutablePath,
        };
        args.AddRange(spec.Arguments);
        return args;
    }

    private static string CombineNormalized(string basePath, string child)
        => basePath.TrimEnd('/', '\\') + "/" + child;

    private static string EscapeSeatbeltString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>POSIX implementation of <see cref="IConfinedProcessRunner"/> using Linux <c>bwrap</c> or macOS <c>sandbox-exec</c>.</summary>
public sealed class PosixConfinedProcessRunner : IConfinedProcessRunner
{
    private readonly ILogger<PosixConfinedProcessRunner>? _logger;

    public PosixConfinedProcessRunner(ILogger<PosixConfinedProcessRunner>? logger = null)
    {
        _logger = logger;
    }

    public ConfinedProcessSession StartSession(ConfinementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);

        Directory.CreateDirectory(spec.WorkingDirectory);
        var tempDir = CreateSessionTempDirectory(spec.SandboxId);
        try
        {
            var (psi, mode) = BuildProcessStartInfo(spec, tempDir, redirectStandardInput: true);
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to launch confined process '{psi.FileName}'.");

            return new PosixConfinedProcessSession(process, mode, tempDir, _logger);
        }
        catch
        {
            TryPurgeDirectory(tempDir, _logger);
            throw;
        }
    }

    public async Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);

        Directory.CreateDirectory(spec.WorkingDirectory);
        var tempDir = CreateSessionTempDirectory(spec.SandboxId);

        try
        {
            var (psi, mode) = BuildProcessStartInfo(spec, tempDir, redirectStandardInput: false);

            var sw = Stopwatch.StartNew();
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to launch confined process '{psi.FileName}'.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var timedOut = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(spec.EffectiveTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                KillEntireProcessTree(process);
            }
            catch (OperationCanceledException)
            {
                KillEntireProcessTree(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            return new ConfinedProcessResult(
                ExitCode: timedOut ? -1 : process.ExitCode,
                StandardOutput: stdout,
                StandardError: stderr,
                TimedOut: timedOut,
                Duration: sw.Elapsed,
                EffectiveMode: mode);
        }
        finally
        {
            TryPurgeDirectory(tempDir, _logger);
        }
    }

    private (ProcessStartInfo StartInfo, ConfinementMode Mode) BuildProcessStartInfo(
        ConfinementSpec spec,
        string tempDir,
        bool redirectStandardInput)
    {
        ConfinementMode mode;
        string fileName;
        IReadOnlyList<string> arguments;

        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/bwrap"))
        {
            mode = ConfinementMode.Bubblewrap;
            fileName = "/usr/bin/bwrap";
            arguments = PosixSandboxCommandBuilder.BuildBubblewrapArguments(spec);
        }
        else if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec"))
        {
            mode = ConfinementMode.Seatbelt;
            fileName = "/usr/bin/sandbox-exec";
            arguments = PosixSandboxCommandBuilder.BuildSeatbeltArguments(spec, tempDir);
        }
        else
        {
            mode = ConfinementMode.PosixSanitizedProcess;
            fileName = spec.ExecutablePath;
            arguments = spec.Arguments;
            _logger?.LogWarning(
                "OS namespace confinement (bwrap/sandbox-exec) unavailable for sandbox {SandboxId}; falling back to sanitized process execution with scrubbed environment and per-session temp-dir purge.",
                spec.SandboxId);
        }

        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (redirectStandardInput)
        {
            psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment.Clear();
        ChildEnvironmentPolicy.Scrub(psi);
        foreach (var (key, value) in PosixSandboxCommandBuilder.BuildCleanPosixEnvironment(spec, tempDir))
        {
            psi.Environment[key] = value;
        }

        return (psi, mode);
    }

    private static string CreateSessionTempDirectory(string sandboxId)
    {
        var sanitized = string.IsNullOrWhiteSpace(sandboxId)
            ? "sbx"
            : new string(sandboxId.Take(32).Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        var tempDir = Path.Combine(Path.GetTempPath(), "intochat-sandbox", $"{sanitized}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void TryPurgeDirectory(string directory, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (logger is not null)
            {
                logger.LogWarning(ex, "Could not recursively purge POSIX sandbox temp directory {TempDirectory}.", directory);
            }
            else
            {
                Trace.TraceWarning("Could not recursively purge POSIX sandbox temp directory '{0}': {1}", directory, ex.Message);
            }
        }
    }

    private static void KillEntireProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(3_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Trace.TraceWarning("PosixConfinedProcessRunner failed to terminate process tree: {0}", ex.Message);
        }
    }

    private sealed class PosixConfinedProcessSession : ConfinedProcessSession
    {
        private readonly Process _process;
        private readonly ILogger? _logger;
        private int _disposed;

        public PosixConfinedProcessSession(
            Process process,
            ConfinementMode effectiveMode,
            string tempDirectory,
            ILogger? logger)
        {
            _process = process;
            EffectiveMode = effectiveMode;
            TempDirectory = tempDirectory;
            _logger = logger;
        }

        public override int ProcessId => _process.Id;

        public override ConfinementMode EffectiveMode { get; }

        public override string TempDirectory { get; }

        public override StreamWriter StandardInput => _process.StandardInput;

        public override StreamReader StandardOutput => _process.StandardOutput;

        public override StreamReader StandardError => _process.StandardError;

        public override bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public override int ExitCode
        {
            get
            {
                try
                {
                    return _process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    return -1;
                }
            }
        }

        public override Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => _process.WaitForExitAsync(cancellationToken);

        public override void Kill() => KillEntireProcessTree(_process);

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            KillEntireProcessTree(_process);
            _process.Dispose();
            TryPurgeDirectory(TempDirectory, _logger);
            return ValueTask.CompletedTask;
        }
    }
}
