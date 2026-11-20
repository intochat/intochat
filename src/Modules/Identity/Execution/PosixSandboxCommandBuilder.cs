// os-only: Linux Bubblewrap and macOS Seatbelt command builders and POSIX confined runner.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IntoChat.OS.Framework.Execution;

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
    public async Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);

        Directory.CreateDirectory(spec.WorkingDirectory);
        var tempDir = Path.Combine(spec.WorkingDirectory, ".sandbox-tmp");
        Directory.CreateDirectory(tempDir);

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
        }

        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

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

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch confined process '{fileName}'.");

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
}
