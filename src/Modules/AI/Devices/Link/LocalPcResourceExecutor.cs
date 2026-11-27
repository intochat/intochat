using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const long MaxReadBytes = 2 * 1024 * 1024;
    private const string ApprovalProofPrefix = "person-approved-v2:";

    private readonly ConcurrentDictionary<string, byte> _spentApprovalPlanIds = new(StringComparer.Ordinal);

    public async Task<string[]> GetConsentedFolderLabelsAsync(CancellationToken ct = default)
    {
        if (!consent.AllowWindowsFolderGateway || rootProvider is null)
        {
            return [];
        }

        var roots = await rootProvider.GetConsentedRootsAsync(IntoChatOSNames.DefaultOwnerBrainId, ct).ConfigureAwait(false);
        return BuildRootMap(roots).Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PcResourceResponse> ExecuteAsync(PcResourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (string.Equals(request.Capability, ClientComputeConsentPolicy.ConsoleCapability, StringComparison.Ordinal))
        {
            return await ExecuteConsoleAsync(request, ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Capability, ClientComputeConsentPolicy.WindowsFolderGatewayCapability, StringComparison.Ordinal))
        {
            return await ExecuteFolderAsync(request, ct).ConfigureAwait(false);
        }

        return Deny($"unsupported-capability: '{request.Capability}' is not supported on this PC.");
    }

    private async Task<PcResourceResponse> ExecuteFolderAsync(PcResourceRequest request, CancellationToken ct)
    {
        if (!consent.AllowWindowsFolderGateway)
        {
            return Deny("consent-disabled: IWindowsFolderGateway is disabled on this PC.");
        }

        if (rootProvider is null)
        {
            return Deny("No consented folder root provider is configured on this PC.");
        }

        FolderResourcePayload payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(request.PayloadJson)
                ? new FolderResourcePayload()
                : JsonSerializer.Deserialize(request.PayloadJson, DeviceWireJson.Default.FolderResourcePayload)
                    ?? new FolderResourcePayload();
        }
        catch (JsonException error)
        {
            logger?.LogWarning(error, "Malformed Mode B folder payload JSON for job {JobId}.", request.JobId);
            return Deny("Invalid folder payload JSON.");
        }

        // Always resolve local PC consented roots under the local owner brain (remote request.BrainId may be a cloud brain id).
        var roots = await rootProvider.GetConsentedRootsAsync(IntoChatOSNames.DefaultOwnerBrainId, ct).ConfigureAwait(false);
        var rootMap = BuildRootMap(roots);
        if (rootMap.Count == 0)
        {
            return Deny("No user-consented folders are available on this PC.");
        }

        var rootLabel = payload.RootLabel?.Trim();
        if (string.IsNullOrWhiteSpace(rootLabel) || !rootMap.TryGetValue(rootLabel, out var hostRoot))
        {
            return Deny($"Folder root '{payload.RootLabel ?? ""}' is not a consented root on this PC.");
        }

        var operation = (request.Operation ?? string.Empty).Trim().ToLowerInvariant();
        try
        {
            var normalizedRoot = PathConfinement.Normalize(hostRoot);
            if (!Directory.Exists(normalizedRoot))
            {
                return Deny($"Consented root '{rootLabel}' does not exist on this PC.");
            }

            var allowRoot = operation is "list" or "search" or "exists" or "stat";
            var fullPath = ResolveConfinedTarget(normalizedRoot, payload.RelativePath, allowRoot);

            return operation switch
            {
                "list" => ListEntries(normalizedRoot, fullPath),
                "read-text" => await ReadTextAsync(fullPath, ct).ConfigureAwait(false),
                "write-text" => await WriteTextAsync(fullPath, payload.Content ?? string.Empty, ct).ConfigureAwait(false),
                "exists" => OkFolder(new FolderResourceResult(Exists: File.Exists(fullPath) || Directory.Exists(fullPath))),
                "stat" => StatEntry(normalizedRoot, fullPath),
                "search" => SearchEntries(normalizedRoot, fullPath, payload.SearchPattern),
                _ => Deny($"Unsupported folder operation '{request.Operation}'."),
            };
        }
        catch (Exception error) when (error is ConfinementDenied or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Deny(error.Message);
        }
    }

    private async Task<PcResourceResponse> ExecuteConsoleAsync(PcResourceRequest request, CancellationToken ct)
    {
        if (!consent.AllowConsole)
        {
            return Deny("consent-disabled: IConsole is disabled on this PC.");
        }

        var operation = (request.Operation ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(operation, "run", StringComparison.Ordinal))
        {
            return Deny($"Unsupported console operation '{request.Operation}'.");
        }

        ConsoleResourcePayload? payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(request.PayloadJson)
                ? null
                : JsonSerializer.Deserialize(request.PayloadJson, DeviceWireJson.Default.ConsoleResourcePayload);
        }
        catch (JsonException error)
        {
            logger?.LogWarning(error, "Malformed Mode B console payload JSON for job {JobId}.", request.JobId);
            return Deny("Invalid console payload JSON.");
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.Command)
            || string.IsNullOrWhiteSpace(payload.ApprovalPlanId)
            || string.IsNullOrWhiteSpace(request.ApprovalToken)
            || !IsValidApprovalProof(
                payload.ApprovalPlanId,
                request.BrainId,
                thisMachine.Value,
                payload.Command,
                payload.RootLabel,
                request.ApprovalToken))
        {
            return Deny("person-approval-required: valid [PersonOnly] approval proof is required before running shell commands.");
        }

        var spentKey = payload.ApprovalPlanId.Trim();
        if (!_spentApprovalPlanIds.TryAdd(spentKey, 0))
        {
            return Deny("person-approval-replay: this [PersonOnly] approval plan has already been used.");
        }

        // Always resolve local PC consented working directory roots under the local owner brain.
        var roots = rootProvider is null
            ? []
            : await rootProvider.GetConsentedRootsAsync(IntoChatOSNames.DefaultOwnerBrainId, ct).ConfigureAwait(false);
        var rootMap = BuildRootMap(roots);
        if (rootMap.Count == 0)
        {
            return Deny("No user-consented working directory roots exist on this PC.");
        }

        string workingDir;
        try
        {
            if (!string.IsNullOrWhiteSpace(payload.RootLabel))
            {
                if (!rootMap.TryGetValue(payload.RootLabel.Trim(), out var labeledRoot))
                {
                    return Deny($"Working directory root '{payload.RootLabel}' is not consented on this PC.");
                }

                workingDir = PathConfinement.Resolve([labeledRoot], string.IsNullOrWhiteSpace(payload.WorkingDirectory) ? labeledRoot : payload.WorkingDirectory);
            }
            else
            {
                var allowedRoots = rootMap.Values.ToArray();
                workingDir = PathConfinement.Resolve(allowedRoots, string.IsNullOrWhiteSpace(payload.WorkingDirectory) ? allowedRoots[0] : payload.WorkingDirectory);
            }

            PathConfinement.RequireNoLinks(workingDir);
        }
        catch (Exception error) when (error is ConfinementDenied or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Deny(error.Message);
        }

        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, MaxTimeoutSeconds);
        try
        {
            var (exitCode, stdout, stderr) = shellRunner is not null
                ? await shellRunner(payload.Command, workingDir, timeoutSeconds, ct).ConfigureAwait(false)
                : await RunCmdExeAsync(payload.Command, workingDir, timeoutSeconds, ct).ConfigureAwait(false);

            return OkConsole(new ConsoleResourceResult(exitCode, Clip(stdout), Clip(stderr)));
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

    private PcResourceResponse ListEntries(string normalizedRoot, string targetDir)
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

        return OkFolder(new FolderResourceResult(Entries: [.. entries]));
    }

    private async Task<PcResourceResponse> ReadTextAsync(string fullPath, CancellationToken ct)
    {
        if (!File.Exists(fullPath))
        {
            return Deny("File was not found inside the consented root.");
        }

        var info = new FileInfo(fullPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Deny("Symlinks and junctions are not permitted.");
        }

        if (info.Length > MaxReadBytes)
        {
            return Deny($"File exceeds maximum readable size ({MaxReadBytes / (1024 * 1024)} MiB).");
        }

        var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        return OkFolder(new FolderResourceResult(TextContent: text));
    }

    private async Task<PcResourceResponse> WriteTextAsync(string fullPath, string content, CancellationToken ct)
    {
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            var attrs = File.GetAttributes(fullPath);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                return Deny("Symlinks and junctions are not permitted.");
            }
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            // Verify every existing ancestor before creating any missing subdirectories.
            var probe = parent;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
            }

            if (!string.IsNullOrEmpty(probe))
            {
                PathConfinement.RequireNoLinks(probe);
            }

            Directory.CreateDirectory(parent);
            PathConfinement.RequireNoLinks(parent);
        }

        await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
        return OkFolder(new FolderResourceResult());
    }

    private PcResourceResponse StatEntry(string normalizedRoot, string fullPath)
    {
        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Deny("Symlinks and junctions are not permitted.");
            }

            var rel = Path.GetRelativePath(normalizedRoot, info.FullName).Replace('\\', '/');
            return OkFolder(new FolderResourceResult(
                Exists: true,
                Entries: [new ConnectedResourceEntry(info.Name, rel, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))]));
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
            return OkFolder(new FolderResourceResult(
                Exists: true,
                Entries: [new ConnectedResourceEntry(info.Name, rel, true, 0L, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))]));
        }

        return OkFolder(new FolderResourceResult(Exists: false, Entries: []));
    }

    private PcResourceResponse SearchEntries(string normalizedRoot, string startDir, string? pattern)
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

        return OkFolder(new FolderResourceResult(Entries: [.. matches]));
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

    internal static string CreateApprovalProof(
        string planId,
        string brainId,
        string machineId,
        string command,
        string? rootLabel = null)
    {
        var normalizedPlanId = planId.Trim();
        var digest = ComputeApprovalDigest(normalizedPlanId, brainId, machineId, command, rootLabel);
        return $"{ApprovalProofPrefix}{normalizedPlanId}:{digest}";
    }

    internal static bool IsValidApprovalProof(
        string planId,
        string brainId,
        string machineId,
        string command,
        string? rootLabel,
        string proof)
    {
        if (string.IsNullOrWhiteSpace(planId)
            || string.IsNullOrWhiteSpace(machineId)
            || string.IsNullOrWhiteSpace(command)
            || string.IsNullOrWhiteSpace(proof))
        {
            return false;
        }

        var expected = CreateApprovalProof(
            planId,
            string.IsNullOrWhiteSpace(brainId) ? IntoChatOSNames.DefaultOwnerBrainId : brainId,
            machineId,
            command,
            rootLabel);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(proof.Trim());
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string ComputeApprovalDigest(
        string planId,
        string brainId,
        string machineId,
        string command,
        string? rootLabel)
    {
        var canonical = string.Join(
            '\0',
            "pc.console.person-only.v2",
            brainId.Trim(),
            machineId.Trim(),
            planId.Trim(),
            command.Trim(),
            (rootLabel ?? string.Empty).Trim().ToLowerInvariant());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCmdExeAsync(
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken ct)
    {
        PathConfinement.RequireNoLinks(workingDirectory);
        var fileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(command);
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }

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

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception drainEx) when (drainEx is OperationCanceledException or IOException or ObjectDisposedException)
            {
                OsConsoleLog.For<LocalPcResourceExecutor>().LogSwallowed(drainEx, "draining timed-out shell output streams");
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"The command ran longer than {timeoutSeconds} seconds.");
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private PcResourceResponse OkFolder(FolderResourceResult result)
        => new(
            Ok: true,
            ResultJson: JsonSerializer.Serialize(result, DeviceWireJson.Default.FolderResourceResult),
            ExecutedByMachineId: thisMachine.Value);

    private PcResourceResponse OkConsole(ConsoleResourceResult result)
        => new(
            Ok: true,
            ResultJson: JsonSerializer.Serialize(result, DeviceWireJson.Default.ConsoleResourceResult),
            ExecutedByMachineId: thisMachine.Value);

    private PcResourceResponse Deny(string reason)
        => new(Ok: false, Error: reason, ExecutedByMachineId: thisMachine.Value);

    private static string Clip(string text) => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars];
}
