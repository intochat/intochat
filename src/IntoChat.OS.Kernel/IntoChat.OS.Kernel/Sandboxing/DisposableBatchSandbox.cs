using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Kernel.Sandboxing;

/// <summary>Execution backend used by <see cref="DisposableBatchSandbox"/> for Tier 3 untrusted batch workloads.</summary>
public enum DisposableSandboxBackend
{
    RootlessDocker = 0,
    WindowsSandboxWsb = 1,
    WinAppCliPackage = 2,
}

/// <summary>
/// Specification for a Tier 3 disposable batch sandbox job with read-only input mount, isolated scratch output mount,
/// strict CPU/RAM/PID quotas, and network disabled by default.
/// </summary>
public sealed record DisposableBatchSandboxSpec(
    string SandboxId,
    string ImageOrPackage,
    string Command,
    IReadOnlyList<string> Arguments,
    string ReadOnlyInputHostPath,
    string ScratchOutputHostPath,
    IReadOnlyDictionary<string, string>? Environment = null,
    int MemoryMb = 2048,
    int CpuCount = 2,
    int PidsLimit = 64,
    bool AllowPublicNetwork = false,
    TimeSpan Timeout = default)
{
    public TimeSpan EffectiveTimeout => Timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : Timeout;
}

/// <summary>Outcome of a Tier 3 disposable batch sandbox run.</summary>
public sealed record DisposableBatchSandboxResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration,
    DisposableSandboxBackend Backend);

/// <summary>
/// Validates host paths before mapping them into Docker volumes, Windows Sandbox <c>.wsb</c> folders, or WinApp CLI v0.7.0
/// package mounts. Blocks sensitive credential/vault/system directories, mount-string colon injection, and symlink escapes.
/// </summary>
public static partial class SandboxHostPathGuard
{
    private static readonly string[] BlockedProfileRelativeSegments =
    [
        ".ssh",
        ".gnupg",
        ".aws",
        ".azure",
        ".kube",
        ".docker",
        ".intochat",
        ".config/gh",
        ".npmrc",
        ".netrc",
        ".gitconfig",
        "AppData/Roaming/Microsoft/Crypto",
        "AppData/Roaming/Microsoft/Credentials",
        "AppData/Local/Microsoft/Credentials",
        "AppData/Roaming/GitHub CLI",
    ];

    private static readonly string[] BlockedPosixRoots =
    [
        "/etc",
        "/root",
        "/proc",
        "/sys",
        "/dev",
        "/boot",
        "/run",
        "/var/run",
        "/usr",
        "/bin",
        "/sbin",
        "/lib",
        "/lib64",
        "/System",
        "/Library",
    ];

    private static readonly string[] BlockedWindowsRoots =
    [
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",
    ];

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePrefix();

    public static string ValidateAndNormalizeHostPath(
        string path,
        bool writable,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Sandbox host mount path cannot be empty.", nameof(path));
        }

        var trimmed = path.Trim();
        if (trimmed.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
        {
            throw new ArgumentException("Sandbox host mount path contains illegal control characters.", nameof(path));
        }

        // Reject relative traversal segments explicitly before normalization.
        var rawSegments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Any(static seg => seg == ".."))
        {
            throw new UnauthorizedAccessException($"Sandbox host mount path '{trimmed}' may not contain '..' traversal segments.");
        }

        var isWindowsDrivePath = WindowsDrivePrefix().IsMatch(trimmed);
        var isPosixAbsolutePath = trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal);

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"UNC or device paths ('{trimmed}') are forbidden in disposable sandbox mounts.");
        }

        if (!isWindowsDrivePath && !isPosixAbsolutePath)
        {
            throw new ArgumentException($"Sandbox host mount path '{trimmed}' must be an absolute host path.", nameof(path));
        }

        // Prevent Docker `-v host:container:mode` colon injection.
        var colonSearchStart = isWindowsDrivePath ? 2 : 0;
        if (trimmed.IndexOf(':', colonSearchStart) >= 0)
        {
            throw new UnauthorizedAccessException($"Sandbox host mount path '{trimmed}' contains an illegal ':' mount separator.");
        }

        var canonicalComparable = NormalizeForComparison(trimmed, isWindowsDrivePath);

        // Reject filesystem roots (`/` or `C:/`).
        if (canonicalComparable == "/" || (isWindowsDrivePath && canonicalComparable.Length <= 3))
        {
            throw new UnauthorizedAccessException($"Mounting filesystem root '{trimmed}' into a sandbox is forbidden.");
        }

        CheckAgainstBlockedLocations(canonicalComparable, trimmed, writable, userProfileOverride, intochatHomeOverride);

        // If the directory or an ancestor already exists on the host OS, resolve symlinks/junctions and re-verify target.
        if ((isWindowsDrivePath && OperatingSystem.IsWindows()) || (isPosixAbsolutePath && !OperatingSystem.IsWindows()))
        {
            var fullHostPath = Path.GetFullPath(trimmed);
            var resolvedRealPath = TryResolveRealPath(fullHostPath);
            if (resolvedRealPath is not null)
            {
                var resolvedComparable = NormalizeForComparison(resolvedRealPath, WindowsDrivePrefix().IsMatch(resolvedRealPath));
                CheckAgainstBlockedLocations(resolvedComparable, trimmed, writable, userProfileOverride, intochatHomeOverride);
            }

            return TrimTrailingSeparators(fullHostPath, isWindowsDrivePath);
        }

        return TrimTrailingSeparators(trimmed, isWindowsDrivePath);
    }

    public static (string ReadOnlyInput, string ScratchOutput) ValidateMountPair(
        string readOnlyInputHostPath,
        string scratchOutputHostPath,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
    {
        var ro = ValidateAndNormalizeHostPath(readOnlyInputHostPath, writable: false, userProfileOverride, intochatHomeOverride);
        var rw = ValidateAndNormalizeHostPath(scratchOutputHostPath, writable: true, userProfileOverride, intochatHomeOverride);

        var roCmp = NormalizeForComparison(ro, WindowsDrivePrefix().IsMatch(ro));
        var rwCmp = NormalizeForComparison(rw, WindowsDrivePrefix().IsMatch(rw));

        if (IsSameOrSubpath(roCmp, rwCmp) || IsSameOrSubpath(rwCmp, roCmp))
        {
            throw new UnauthorizedAccessException(
                $"Sandbox read-only input path '{ro}' and writable scratch output path '{rw}' must be disjoint directories.");
        }

        return (ro, rw);
    }

    private static void CheckAgainstBlockedLocations(
        string candidateComparable,
        string originalDisplayPath,
        bool writable,
        string? userProfileOverride,
        string? intochatHomeOverride)
    {
        var profile = !string.IsNullOrWhiteSpace(userProfileOverride)
            ? userProfileOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var profileCmp = NormalizeForComparison(profile, WindowsDrivePrefix().IsMatch(profile));

            // Mounting the user's home root itself (or an ancestor of it) exposes ~/.ssh and ~/.intochat/vault!
            if (IsSameOrSubpath(profileCmp, candidateComparable))
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox mount '{originalDisplayPath}' encompasses the user profile directory '{profile}', which exposes sensitive credentials.");
            }

            foreach (var rel in BlockedProfileRelativeSegments)
            {
                var blockedFull = profileCmp.TrimEnd('/') + "/" + rel;
                if (IsSameOrSubpath(candidateComparable, blockedFull) || IsSameOrSubpath(blockedFull, candidateComparable))
                {
                    throw new UnauthorizedAccessException(
                        $"Sandbox mount '{originalDisplayPath}' is denied because it overlaps sensitive credential path '{blockedFull}'.");
                }
            }
        }

        var intochatHome = !string.IsNullOrWhiteSpace(intochatHomeOverride)
            ? intochatHomeOverride
            : Environment.GetEnvironmentVariable("IntoChatOS__Home");
        if (!string.IsNullOrWhiteSpace(intochatHome))
        {
            var homeCmp = NormalizeForComparison(intochatHome, WindowsDrivePrefix().IsMatch(intochatHome));
            if (IsSameOrSubpath(candidateComparable, homeCmp) || IsSameOrSubpath(homeCmp, candidateComparable))
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox mount '{originalDisplayPath}' is denied because it overlaps IntoChat OS custody root '{intochatHome}'.");
            }
        }

        // Catch embedded sensitive directory segments regardless of which user profile path was passed (e.g. ~/.ssh, ~/.intochat/vault).
        var segments = candidateComparable.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Equals(".ssh", StringComparison.OrdinalIgnoreCase)
                || seg.Equals(".gnupg", StringComparison.OrdinalIgnoreCase)
                || seg.Equals(".aws", StringComparison.OrdinalIgnoreCase)
                || seg.Equals(".azure", StringComparison.OrdinalIgnoreCase)
                || seg.Equals(".kube", StringComparison.OrdinalIgnoreCase)
                || seg.Equals(".intochat", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox mount '{originalDisplayPath}' references protected directory segment '{seg}'.");
            }
        }

        foreach (var winSys in BlockedWindowsRoots)
        {
            var sysCmp = NormalizeForComparison(winSys, isWindowsDrivePath: true);
            if (IsSameOrSubpath(candidateComparable, sysCmp) || IsSameOrSubpath(sysCmp, candidateComparable))
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox mount '{originalDisplayPath}' overlaps protected Windows system directory '{winSys}'.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var actualWinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(actualWinDir))
            {
                var winCmp = NormalizeForComparison(actualWinDir, isWindowsDrivePath: true);
                if (IsSameOrSubpath(candidateComparable, winCmp) || IsSameOrSubpath(winCmp, candidateComparable))
                {
                    throw new UnauthorizedAccessException(
                        $"Sandbox mount '{originalDisplayPath}' overlaps Windows directory '{actualWinDir}'.");
                }
            }
        }

        foreach (var posixSys in BlockedPosixRoots)
        {
            if (IsSameOrSubpath(candidateComparable, posixSys))
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox mount '{originalDisplayPath}' overlaps protected system directory '{posixSys}'.");
            }
        }

        if (writable && candidateComparable.StartsWith("/opt/", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Writable sandbox mount '{originalDisplayPath}' may not target read-only package tree '/opt'.");
        }
    }

    private static string? TryResolveRealPath(string fullPath)
    {
        try
        {
            var current = fullPath;
            var tailSegments = new Stack<string>();
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current) && !File.Exists(current))
            {
                var name = Path.GetFileName(current);
                if (!string.IsNullOrEmpty(name))
                {
                    tailSegments.Push(name);
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                current = parent;
            }

            if (string.IsNullOrEmpty(current))
            {
                return null;
            }

            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var resolvedBase = target?.FullName ?? info.FullName;
            while (tailSegments.TryPop(out var child))
            {
                resolvedBase = Path.Combine(resolvedBase, child);
            }

            return resolvedBase;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _ = ex.Message;
            return null;
        }
    }

    private static bool IsSameOrSubpath(string candidate, string root)
    {
        var normCandidate = candidate.TrimEnd('/');
        var normRoot = root.TrimEnd('/');
        if (string.Equals(normCandidate, normRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normCandidate.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForComparison(string path, bool isWindowsDrivePath)
    {
        var slashNormalized = path.Replace('\\', '/');
        while (slashNormalized.Contains("//", StringComparison.Ordinal))
        {
            slashNormalized = slashNormalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (slashNormalized.Length > 1)
        {
            slashNormalized = slashNormalized.TrimEnd('/');
        }

        if (isWindowsDrivePath && slashNormalized.Length == 2 && slashNormalized[1] == ':')
        {
            return slashNormalized + "/";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !isWindowsDrivePath
            ? slashNormalized
            : slashNormalized;
    }

    private static string TrimTrailingSeparators(string path, bool isWindowsDrivePath)
    {
        if (isWindowsDrivePath && path.Length <= 3)
        {
            return path;
        }

        if (!isWindowsDrivePath && path == "/")
        {
            return path;
        }

        return path.TrimEnd('/', '\\');
    }
}

/// <summary>
/// Unified Tier 3 disposable batch sandbox facade providing hardened rootless Docker execution,
/// Windows Sandbox (<c>.wsb</c>) generation, and Microsoft WinApp CLI v0.7.0 sparse-package isolation manifests.
/// </summary>
public sealed class DisposableBatchSandbox
{
    private readonly ContainerBatchSandboxRunner _containerRunner;

    public DisposableBatchSandbox(ContainerBatchSandboxRunner? containerRunner = null)
    {
        _containerRunner = containerRunner ?? new ContainerBatchSandboxRunner();
    }

    public IReadOnlyList<string> BuildDockerArguments(
        DisposableBatchSandboxSpec spec,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
        => ContainerBatchSandboxRunner.BuildDockerRunArguments(spec, userProfileOverride, intochatHomeOverride);

    public string BuildWindowsSandboxWsb(
        DisposableBatchSandboxSpec spec,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
        => WindowsSandboxWsbGenerator.BuildWsbXml(spec, userProfileOverride, intochatHomeOverride);

    public WinAppPackageIsolationManifest BuildWinAppIsolationManifest(
        DisposableBatchSandboxSpec spec,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
        => WindowsSandboxWsbGenerator.BuildWinAppPackageConfig(spec, userProfileOverride, intochatHomeOverride);

    public Task<DisposableBatchSandboxResult> RunContainerBatchAsync(
        DisposableBatchSandboxSpec spec,
        CancellationToken cancellationToken = default)
        => _containerRunner.RunAsync(spec, cancellationToken);
}
