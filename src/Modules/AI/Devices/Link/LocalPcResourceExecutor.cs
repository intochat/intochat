using System.Diagnostics;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Executes Mode B PC resource operations (<c>IWindowsFolderGateway</c> and <c>[PersonOnly]</c>-gated <c>IConsole</c>)
/// locally on this Windows PC, strictly enforcing <see cref="PathConfinement.Resolve"/> against user-consented roots
/// and fail-closed consent &amp; approval proof checks.
/// </summary>
internal sealed class LocalPcResourceExecutor(
    MachineId thisMachine,
    IClientResourceConsent consent,
    IConsentedFolderRootProvider? rootProvider = null,
    Func<string, string, int, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? shellRunner = null,
    ILogger<LocalPcResourceExecutor>? logger = null) : ILocalPcResourceExecutor
{
    private const int MaxOutputChars = 20_000;
    private const int MaxTimeoutSeconds = 270;

    public async Task<string[]> GetConsentedFolderLabelsAsync(CancellationToken ct = default)
    {
        if (!consent.AllowWindowsFolderGateway || rootProvider is null)
        {
            return [];
        }

        var roots = await rootProvider.GetConsentedRootsAsync(IntoChatOSNames.DefaultOwnerBrainId, ct).ConfigureAwait(false);
        return BuildRootMap(roots).Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ConnectedResourceResponse> ExecuteAsync(ConnectedResourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (request.Kind == ConnectedResourceKind.ConsoleRun)
        {
            return await ExecuteConsoleAsync(request, ct).ConfigureAwait(false);
        }

        return await ExecuteFolderAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<ConnectedResourceResponse> ExecuteFolderAsync(ConnectedResourceRequest request, CancellationToken ct)
    {
        if (!consent.AllowWindowsFolderGateway)
        {
            return Deny("consent-disabled: IWindowsFolderGateway is disabled on this PC.");
        }

        if (rootProvider is null)
        {
            return Deny("No consented folder root provider is configured on this PC.");
        }

        var brainId = string.IsNullOrWhiteSpace(request.BrainId) ? IntoChatOSNames.DefaultOwnerBrainId : request.BrainId.Trim();
        var roots = await rootProvider.GetConsentedRootsAsync(brainId, ct).ConfigureAwait(false);
        var rootMap = BuildRootMap(roots);
        if (rootMap.Count == 0)
        {
            return Deny("No user-consented folders are available on this PC.");
        }

        var rootLabel = request.RootLabel?.Trim();
        if (string.IsNullOrWhiteSpace(rootLabel) || !rootMap.TryGetValue(rootLabel, out var hostRoot))
        {
            return Deny($"Folder root '{request.RootLabel ?? ""}' is not a consented root on this PC.");
        }

        try
        {
            var normalizedRoot = PathConfinement.Normalize(hostRoot);
            if (!Directory.Exists(normalizedRoot))
            {
                return Deny($"Consented root '{rootLabel}' does not exist on this PC.");
            }

            var allowRoot = request.Kind is ConnectedResourceKind.FolderList or ConnectedResourceKind.FolderSearch or ConnectedResourceKind.FolderExists or ConnectedResourceKind.FolderStat;
            var fullPath = ResolveConfinedTarget(normalizedRoot, request.RelativePath, allowRoot);

            return request.Kind switch
            {
                ConnectedResourceKind.FolderList => ListEntries(normalizedRoot, fullPath),
                ConnectedResourceKind.FolderReadText => await ReadTextAsync(fullPath, ct).ConfigureAwait(false),
                ConnectedResourceKind.FolderWriteText => await WriteTextAsync(fullPath, request.Content ?? string.Empty, ct).ConfigureAwait(false),
                ConnectedResourceKind.FolderExists => new ConnectedResourceResponse(true, Exists: File.Exists(fullPath) || Directory.Exists(fullPath), ExecutedByMachineId: thisMachine.Value),
                ConnectedResourceKind.FolderStat => StatEntry(normalizedRoot, fullPath),
                ConnectedResourceKind.FolderSearch => SearchEntries(normalizedRoot, fullPath, request.SearchPattern),
                _ => Deny($"Unsupported folder operation '{request.Kind}'."),
            };
        }
        catch (Exception error) when (error is ConfinementDenied or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Deny(error.Message);
        }
    }

    private async Task<ConnectedResourceResponse> ExecuteConsoleAsync(ConnectedResourceRequest request, CancellationToken ct)
    {
        if (!consent.AllowConsole)
        {
            return Deny("consent-disabled: IConsole is disabled on this PC.");
        }

        if (string.IsNullOrWhiteSpace(request.ApprovalPlanId)
            || string.IsNullOrWhiteSpace(request.ApprovalProof)
            || !IsValidApprovalProof(request.ApprovalPlanId, request.BrainId, request.ApprovalProof))
        {
            return Deny("person-approval-required: valid [PersonOnly] approval proof is required before running shell commands.");
        }

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return Deny("Command is required.");
        }

        var brainId = string.IsNullOrWhiteSpace(request.BrainId) ? IntoChatOSNames.DefaultOwnerBrainId : request.BrainId.Trim();
        var roots = rootProvider is null
            ? []
            : await rootProvider.GetConsentedRootsAsync(brainId, ct).ConfigureAwait(false);
        var rootMap = BuildRootMap(roots);
        if (rootMap.Count == 0)
        {
            return Deny("No user-consented working directory roots exist on this PC.");
        }

        string workingDir;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.RootLabel))
            {
                if (!rootMap.TryGetValue(request.RootLabel.Trim(), out var labeledRoot))
                {
                    return Deny($"Working directory root '{request.RootLabel}' is not consented on this PC.");
                }

                workingDir = PathConfinement.Resolve([labeledRoot], string.IsNullOrWhiteSpace(request.WorkingDirectory) ? labeledRoot : request.WorkingDirectory);
            }
            else
            {
                var allowedRoots = rootMap.Values.ToArray();
                workingDir = PathConfinement.Resolve(allowedRoots, string.IsNullOrWhiteSpace(request.WorkingDirectory) ? allowedRoots[0] : request.WorkingDirectory);
            }
        }
        catch (Exception error) when (error is ConfinementDenied or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Deny(error.Message);
        }

        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, MaxTimeoutSeconds);
        try
        {
            var (exitCode, stdout, stderr) = shellRunner is not null
                ? await shellRunner(request.Command, workingDir, timeoutSeconds, ct).ConfigureAwait(false)
                : await RunCmdExeAsync(request.Command, workingDir, timeoutSeconds, ct).ConfigureAwait(false);

            return new ConnectedResourceResponse(
                Handled: true,
                ExitCode: exitCode,
                StandardOutput: Clip(stdout),
                StandardError: Clip(stderr),
                ExecutedByMachineId: thisMachine.Value);
        }
        catch (TimeoutException ex)
        {
            return Deny(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Mode B local console execution failed.");
            return Deny(ex.Message);
        }
    }

    private ConnectedResourceResponse ListEntries(string normalizedRoot, string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            return Deny("Directory was not found inside the consented root.");
        }

        var entries = new List<ConnectedResourceEntry>();
        foreach (var entry in new DirectoryInfo(targetDir).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var isDir = entry is DirectoryInfo;
            var size = isDir ? 0L : ((FileInfo)entry).Length;
            var rel = Path.GetRelativePath(normalizedRoot, entry.FullName).Replace('\\', '/');
            entries.Add(new ConnectedResourceEntry(entry.Name, rel, isDir, size, new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        entries.Sort(static (a, b) =>
        {
            var cmp = b.IsDirectory.CompareTo(a.IsDirectory);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        return new ConnectedResourceResponse(true, Entries: [.. entries], ExecutedByMachineId: thisMachine.Value);
    }

    private async Task<ConnectedResourceResponse> ReadTextAsync(string fullPath, CancellationToken ct)
    {
        if (!File.Exists(fullPath))
        {
            return Deny("File was not found inside the consented root.");
        }

        var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        return new ConnectedResourceResponse(true, TextContent: text, ExecutedByMachineId: thisMachine.Value);
    }

    private async Task<ConnectedResourceResponse> WriteTextAsync(string fullPath, string content, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
            PathConfinement.RequireNoLinks(parent);
        }

        await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
        return new ConnectedResourceResponse(true, ExecutedByMachineId: thisMachine.Value);
    }

    private ConnectedResourceResponse StatEntry(string normalizedRoot, string fullPath)
    {
        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Deny("Symlinks and junctions are not permitted.");
            }

            var rel = Path.GetRelativePath(normalizedRoot, info.FullName).Replace('\\', '/');
            return new ConnectedResourceResponse(
                true,
                Exists: true,
                Entries: [new ConnectedResourceEntry(info.Name, rel, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))],
                ExecutedByMachineId: thisMachine.Value);
        }

        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Deny("Symlinks and junctions are not permitted.");
            }

            var rel = string.Equals(PathConfinement.Normalize(info.FullName), normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? ""
                : Path.GetRelativePath(normalizedRoot, info.FullName).Replace('\\', '/');
            return new ConnectedResourceResponse(
                true,
                Exists: true,
                Entries: [new ConnectedResourceEntry(info.Name, rel, true, 0L, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))],
                ExecutedByMachineId: thisMachine.Value);
        }

        return new ConnectedResourceResponse(true, Exists: false, Entries: [], ExecutedByMachineId: thisMachine.Value);
    }

    private ConnectedResourceResponse SearchEntries(string normalizedRoot, string startDir, string? pattern)
    {
        if (!Directory.Exists(startDir))
        {
            return Deny("Search directory was not found inside the consented root.");
        }

        var needle = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        var matches = new List<ConnectedResourceEntry>();
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(new DirectoryInfo(startDir));

        while (queue.Count > 0 && matches.Count < 200)
        {
            var current = queue.Dequeue();
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo subDir)
                {
                    queue.Enqueue(subDir);
                }

                if (needle == "*" || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var isDir = entry is DirectoryInfo;
                    var size = isDir ? 0L : ((FileInfo)entry).Length;
                    var rel = Path.GetRelativePath(normalizedRoot, entry.FullName).Replace('\\', '/');
                    matches.Add(new ConnectedResourceEntry(entry.Name, rel, isDir, size, new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero)));
                    if (matches.Count >= 200)
                    {
                        break;
                    }
                }
            }
        }

        return new ConnectedResourceResponse(true, Entries: [.. matches], ExecutedByMachineId: thisMachine.Value);
    }

    private static string ResolveConfinedTarget(string normalizedRoot, string? relativePath, bool allowRoot)
    {
        var trimmed = (relativePath ?? string.Empty).Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, ".", StringComparison.Ordinal))
        {
            if (!allowRoot)
            {
                throw new UnauthorizedAccessException("A non-root relative path is required.");
            }

            return PathConfinement.Resolve([normalizedRoot], normalizedRoot);
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || Path.IsPathRooted(trimmed)
            || Path.IsPathFullyQualified(trimmed)
            || trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path '{trimmed}' must be relative to the consented folder root.");
        }

        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal)
                || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Path traversal segment in '{trimmed}' is not permitted.");
            }
        }

        var osRelative = trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var resolved = PathConfinement.Resolve([normalizedRoot], osRelative);
        PathConfinement.RequireNoLinks(resolved);
        return resolved;
    }

    internal static Dictionary<string, string> BuildRootMap(string[]? roots)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (roots is not { Length: > 0 })
        {
            return map;
        }

        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = PathConfinement.Normalize(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                OsConsoleLog.For<LocalPcResourceExecutor>().LogSwallowed(ex, "skipping malformed consented root path");
                continue;
            }

            var baseName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "root";
            }

            var label = baseName;
            var suffix = 2;
            while (map.ContainsKey(label))
            {
                label = $"{baseName}-{suffix++}";
            }

            map[label] = normalized;
        }

        return map;
    }

    internal static string CreateApprovalProof(string planId, string brainId)
        => $"person-approved:{brainId.Trim()}:{planId.Trim()}";

    internal static bool IsValidApprovalProof(string planId, string brainId, string proof)
        => string.Equals(proof.Trim(), CreateApprovalProof(planId, string.IsNullOrWhiteSpace(brainId) ? IntoChatOSNames.DefaultOwnerBrainId : brainId), StringComparison.Ordinal);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCmdExeAsync(
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var fileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows() ? "/c " + command : "-c \"" + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var start = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ChildEnvironmentPolicy.Scrub(start);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception killEx) when (killEx is InvalidOperationException or NotSupportedException)
            {
                OsConsoleLog.For<LocalPcResourceExecutor>().LogSwallowed(killEx, "terminating timed-out shell process");
            }
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"The command ran longer than {timeoutSeconds} seconds.");
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private ConnectedResourceResponse Deny(string reason)
        => new(Handled: false, Error: reason, ExecutedByMachineId: thisMachine.Value);

    private static string Clip(string text) => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars];
}
