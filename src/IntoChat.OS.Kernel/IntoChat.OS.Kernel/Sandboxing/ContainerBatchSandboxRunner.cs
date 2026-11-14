using System.Globalization;
using System.Text.RegularExpressions;
using IntoChat.OS.Framework.Execution;

namespace IntoChat.OS.Kernel.Sandboxing;

/// <summary>
/// Builds and executes hardened Tier 3 rootless Docker batch containers with read-only rootfs, dropped Linux capabilities,
/// <c>no-new-privileges</c>, disabled networking, bounded PID/RAM/CPU quotas, scrubbed environment, and strict host path validation.
/// </summary>
public sealed partial class ContainerBatchSandboxRunner
{
    public const string DefaultDockerExecutable = "docker";
    public const string NonRootUserSpec = "65532:65532";

    private readonly IConfinedProcessRunner _processRunner;
    private readonly string _dockerExecutablePath;
    private readonly string? _userProfileOverride;
    private readonly string? _intochatHomeOverride;

    public ContainerBatchSandboxRunner(
        IConfinedProcessRunner? processRunner = null,
        string? dockerExecutablePath = null,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
    {
        _processRunner = processRunner ?? CreateDefaultProcessRunner();
        _dockerExecutablePath = string.IsNullOrWhiteSpace(dockerExecutablePath)
            ? DefaultDockerExecutable
            : dockerExecutablePath;
        _userProfileOverride = userProfileOverride;
        _intochatHomeOverride = intochatHomeOverride;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_.-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeContainerNameChar();

    public static string BuildContainerName(string sandboxId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxId);
        var sanitized = UnsafeContainerNameChar().Replace(sandboxId.Trim(), "-").Trim('-', '.');
        if (string.IsNullOrEmpty(sanitized))
        {
            throw new ArgumentException("SandboxId must contain at least one alphanumeric character.", nameof(sandboxId));
        }

        if (sanitized.Length > 40)
        {
            sanitized = sanitized[..40];
        }

        return "intochat-sbx-" + sanitized.ToLowerInvariant();
    }

    public static string FormatDockerMemoryLimit(int memoryMb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryMb, 128);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memoryMb, 65536);

        return memoryMb % 1024 == 0
            ? (memoryMb / 1024).ToString(CultureInfo.InvariantCulture) + "g"
            : memoryMb.ToString(CultureInfo.InvariantCulture) + "m";
    }

    public static IReadOnlyList<string> BuildDockerRunArguments(
        DisposableBatchSandboxSpec spec,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
    {
        ValidateSpecCore(spec);

        var (roInput, scratchOut) = SandboxHostPathGuard.ValidateMountPair(
            spec.ReadOnlyInputHostPath,
            spec.ScratchOutputHostPath,
            userProfileOverride,
            intochatHomeOverride);

        var containerName = BuildContainerName(spec.SandboxId);
        var memoryLimit = FormatDockerMemoryLimit(spec.MemoryMb);
        var networkMode = spec.AllowPublicNetwork ? "bridge" : "none";

        var args = new List<string>
        {
            "run",
            "--rm",
            "--name", containerName,
            "--network", networkMode,
            "--read-only",
            "--pids-limit", spec.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", memoryLimit,
            "--memory-swap", memoryLimit,
            "--cpus", spec.CpuCount.ToString(CultureInfo.InvariantCulture),
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", NonRootUserSpec,
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
            "--workdir", "/out",
            "-v", $"{roInput}:/in:ro",
            "-v", $"{scratchOut}:/out:rw",
        };

        if (spec.Environment is { Count: > 0 })
        {
            var sortedCleanEnv = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in spec.Environment)
            {
                if (!PosixSandboxCommandBuilder.IsBlockedSecretVariableName(key)
                    && !key.Contains('=', StringComparison.Ordinal)
                    && !key.Contains('\0', StringComparison.Ordinal))
                {
                    sortedCleanEnv[key] = (value ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);
                }
            }

            foreach (var (key, value) in sortedCleanEnv)
            {
                args.Add("-e");
                args.Add($"{key}={value}");
            }
        }

        args.Add("--");
        args.Add(spec.ImageOrPackage.Trim());
        args.Add(spec.Command.Trim());
        foreach (var argument in spec.Arguments)
        {
            args.Add(argument ?? string.Empty);
        }

        return args;
    }

    public async Task<DisposableBatchSandboxResult> RunAsync(
        DisposableBatchSandboxSpec spec,
        CancellationToken cancellationToken = default)
    {
        var dockerArgs = BuildDockerRunArguments(spec, _userProfileOverride, _intochatHomeOverride);
        var scratchDir = Path.GetFullPath(spec.ScratchOutputHostPath);
        Directory.CreateDirectory(scratchDir);

        var confinementSpec = new ConfinementSpec(
            SandboxId: "docker-" + spec.SandboxId,
            ExecutablePath: _dockerExecutablePath,
            Arguments: dockerArgs,
            WorkingDirectory: scratchDir,
            ReadOnlyPaths: [Path.GetFullPath(spec.ReadOnlyInputHostPath)],
            ReadWritePaths: [scratchDir],
            CleanEnvironment: new Dictionary<string, string>(StringComparer.Ordinal),
            Network: spec.AllowPublicNetwork ? NetworkPolicy.PublicInternetOnly : NetworkPolicy.None,
            MaxMemoryBytes: 512L * 1024 * 1024,
            MaxProcesses: 16,
            Timeout: spec.EffectiveTimeout);

        var processResult = await _processRunner.RunAsync(confinementSpec, cancellationToken).ConfigureAwait(false);

        if (processResult.TimedOut && string.Equals(_dockerExecutablePath, DefaultDockerExecutable, StringComparison.OrdinalIgnoreCase))
        {
            await TryForceRemoveContainerAsync(BuildContainerName(spec.SandboxId), scratchDir).ConfigureAwait(false);
        }

        return new DisposableBatchSandboxResult(
            ExitCode: processResult.ExitCode,
            StandardOutput: processResult.StandardOutput,
            StandardError: processResult.StandardError,
            TimedOut: processResult.TimedOut,
            Duration: processResult.Duration,
            Backend: DisposableSandboxBackend.RootlessDocker);
    }

    internal static void ValidateSpecCore(DisposableBatchSandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.SandboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ImageOrPackage);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Command);
        ArgumentNullException.ThrowIfNull(spec.Arguments);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.MemoryMb, 128);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spec.MemoryMb, 65536);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.CpuCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spec.CpuCount, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.PidsLimit, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spec.PidsLimit, 4096);

        if (spec.ImageOrPackage.TrimStart().StartsWith('-'))
        {
            throw new ArgumentException("Container image or package identifier cannot start with '-'.", nameof(spec));
        }
    }

    private async Task TryForceRemoveContainerAsync(string containerName, string workingDirectory)
    {
        try
        {
            var cleanupSpec = new ConfinementSpec(
                SandboxId: "cleanup-" + containerName,
                ExecutablePath: _dockerExecutablePath,
                Arguments: ["rm", "-f", containerName],
                WorkingDirectory: workingDirectory,
                ReadOnlyPaths: [],
                ReadWritePaths: [workingDirectory],
                CleanEnvironment: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(3));
            _ = await _processRunner.RunAsync(cleanupSpec, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _ = ex.Message;
        }
    }

    private static IConfinedProcessRunner CreateDefaultProcessRunner()
        => OperatingSystem.IsWindows()
            ? new WindowsAppContainerRunner()
            : new PosixConfinedProcessRunner();
}
