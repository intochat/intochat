using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class DesktopBootstrap
{
    /// <summary>
    /// Atomically writes <c>~/.intochat/run/kernel.pid</c> via temporary sibling + <see cref="File.Move(string,string,bool)"/>
    /// with owner-only file ACLs.
    /// </summary>
    public static void WriteKernelPidRecord(
        string pidFilePath,
        int pid,
        int port,
        int? unitHostPid = null,
        bool background = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pidFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pid, 0);

        var fullPath = Path.GetFullPath(pidFilePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            RestrictDirectoryToCurrentUser(directory);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("pid", pid);
            writer.WriteNumber("port", port);
            if (unitHostPid is > 0)
            {
                writer.WriteNumber("unitHostPid", unitHostPid.Value);
            }

            writer.WriteBoolean("background", background);
            writer.WriteString("startedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        var tempPath = fullPath + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + RandomNumberGenerator.GetInt32(100_000).ToString(CultureInfo.InvariantCulture);
        File.WriteAllBytes(tempPath, buffer.ToArray());
        RestrictFileToCurrentUser(tempPath);
        File.Move(tempPath, fullPath, overwrite: true);
        RestrictFileToCurrentUser(fullPath);
    }

    public static bool TryReadKernelPidRecord(string pidFilePath, out int pid, out int port)
    {
        if (TryReadKernelPidRecord(pidFilePath, out var record))
        {
            pid = record.Pid;
            port = record.Port;
            return true;
        }

        pid = 0;
        port = 0;
        return false;
    }

    public static bool TryReadKernelPidRecord(string pidFilePath, out KernelPidRecord record)
    {
        record = default!;
        if (string.IsNullOrWhiteSpace(pidFilePath) || !File.Exists(pidFilePath))
        {
            return false;
        }

        try
        {
            var rawBytes = File.ReadAllBytes(pidFilePath);
            ReadOnlySpan<byte> utf8Span = rawBytes;
            if (utf8Span.StartsWith("\uFEFF"u8))
            {
                utf8Span = utf8Span[3..];
            }

            var trimmedText = Encoding.UTF8.GetString(utf8Span).Trim();
            if (int.TryParse(trimmedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plainPid) && plainPid > 0)
            {
                record = new KernelPidRecord(plainPid, 0, null, false, null);
                return true;
            }

            using var doc = JsonDocument.Parse(rawBytes.AsMemory(rawBytes.Length - utf8Span.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("pid", out var pidProp)
                || !pidProp.TryGetInt32(out var pid)
                || pid <= 0)
            {
                return false;
            }

            var port = root.TryGetProperty("port", out var portProp) && portProp.TryGetInt32(out var parsedPort)
                ? parsedPort
                : 0;
            int? unitHostPid = root.TryGetProperty("unitHostPid", out var uhProp)
                && uhProp.ValueKind == JsonValueKind.Number
                && uhProp.TryGetInt32(out var parsedUh)
                && parsedUh > 0
                ? parsedUh
                : null;
            var background = root.TryGetProperty("background", out var bgProp) && bgProp.ValueKind == JsonValueKind.True;
            var startedAt = root.TryGetProperty("startedAt", out var stProp) && stProp.ValueKind == JsonValueKind.String
                ? stProp.GetString()
                : null;

            record = new KernelPidRecord(pid, port, unitHostPid, background, startedAt);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to read kernel PID file '{0}': {1}", pidFilePath, ex.Message);
            return false;
        }
    }

    public static bool TryReadLayoutPidRecord(DesktopLayout layout, out KernelPidRecord record)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (TryReadKernelPidRecord(layout.KernelPidFilePath, out record))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(layout.LegacyKernelPidFilePath)
            && TryReadKernelPidRecord(layout.LegacyKernelPidFilePath, out record))
        {
            return true;
        }

        record = default!;
        return false;
    }

    /// <summary>
    /// Sub-5ms non-blocking kernel health, socket, and PID probe for <c>intochat --status</c>.
    /// </summary>
    public static DesktopStatusSnapshot ProbeStatus(
        DesktopLayout layout,
        int configuredPort,
        Func<int, bool>? isPortListening = null,
        Func<int, bool>? isProcessAlive = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var sw = Stopwatch.StartNew();

        var checkPort = isPortListening ?? IsPortListening;
        var checkPid = isProcessAlive ?? IsProcessAlive;

        var hasRecord = TryReadLayoutPidRecord(layout, out var record);
        var effectivePort = hasRecord && record.Port is > 0 and <= 65535 ? record.Port : configuredPort;
        var listening = checkPort(effectivePort);
        var pidAlive = hasRecord && checkPid(record.Pid);
        var unitHostPid = hasRecord && record.UnitHostPid is > 0 && checkPid(record.UnitHostPid.Value)
            ? record.UnitHostPid
            : null;
        var presencePresent = ReadPresenceToken(layout.PresenceFilePath) is not null;

        var stalePidFile = hasRecord && !pidAlive && !listening;
        var healthy = listening && (!hasRecord || pidAlive);
        var status = healthy
            ? "running"
            : listening
                ? "degraded"
                : pidAlive
                    ? "starting"
                    : "stopped";

        sw.Stop();
        return new DesktopStatusSnapshot(
            Status: status,
            Healthy: healthy,
            Listening: listening,
            Pid: hasRecord && pidAlive ? record.Pid : null,
            TrackedPid: hasRecord ? record.Pid : null,
            UnitHostPid: unitHostPid,
            Port: effectivePort,
            Background: hasRecord && record.Background,
            PresencePresent: presencePresent,
            StalePidFile: stalePidFile,
            HomeDirectory: layout.HomeDirectory,
            ProbeMs: Math.Round(sw.Elapsed.TotalMilliseconds, 3));
    }

    public static string FormatStatus(DesktopStatusSnapshot snapshot, bool textFormat = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (textFormat)
        {
            var pidLabel = snapshot.Pid?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var modeLabel = snapshot.Background ? "background" : "foreground";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"IntoChat OS: {snapshot.Status} (healthy={snapshot.Healthy.ToString().ToLowerInvariant()}, pid={pidLabel}, port={snapshot.Port}, mode={modeLabel}, probe={snapshot.ProbeMs:F2}ms)");
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("status", snapshot.Status);
            json.WriteBoolean("healthy", snapshot.Healthy);
            json.WriteBoolean("listening", snapshot.Listening);
            if (snapshot.Pid is { } pid)
            {
                json.WriteNumber("pid", pid);
            }
            else
            {
                json.WriteNull("pid");
            }

            if (snapshot.UnitHostPid is { } uhPid)
            {
                json.WriteNumber("unitHostPid", uhPid);
            }
            else
            {
                json.WriteNull("unitHostPid");
            }

            json.WriteNumber("port", snapshot.Port);
            json.WriteBoolean("background", snapshot.Background);
            json.WriteBoolean("presencePresent", snapshot.PresencePresent);
            json.WriteBoolean("stalePidFile", snapshot.StalePidFile);
            json.WriteString("home", snapshot.HomeDirectory);
            json.WriteNumber("probeMs", snapshot.ProbeMs);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Authenticates with <c>~/.intochat/run/presence</c> to request graceful kernel shutdown at
    /// <c>POST /runtime/shutdown</c>, waits for drain, terminates any remaining tracked kernel or UnitHost PID,
    /// and removes stale <c>run/kernel.pid</c> and <c>run/presence</c>.
    /// </summary>
    public static async Task<DesktopStopResult> StopRunningKernelAsync(
        DesktopLayout layout,
        int configuredPort,
        HttpMessageHandler? httpHandler = null,
        Action<int>? killProcessTree = null,
        Func<int, bool>? isProcessAlive = null,
        Func<int, bool>? isPortListening = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var checkPort = isPortListening ?? IsPortListening;
        var checkPid = isProcessAlive ?? IsProcessAlive;
        var killer = killProcessTree ?? KillProcessTreeById;

        var hasRecord = TryReadLayoutPidRecord(layout, out var record);
        var effectivePort = hasRecord && record.Port is > 0 and <= 65535 ? record.Port : configuredPort;
        var presenceToken = ReadPresenceToken(layout.PresenceFilePath);

        var gracefulAccepted = false;
        if (presenceToken is not null && (httpHandler is not null || checkPort(effectivePort)))
        {
            gracefulAccepted = await TryRequestGracefulShutdownAsync(
                effectivePort,
                presenceToken,
                httpHandler,
                cancellationToken).ConfigureAwait(false);
        }

        if (gracefulAccepted && hasRecord && record.Pid > 0)
        {
            var graceDeadline = Environment.TickCount64 + 2_000;
            while (Environment.TickCount64 < graceDeadline && checkPid(record.Pid))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        var terminatedUnitHost = false;
        if (hasRecord && record.UnitHostPid is > 0 && checkPid(record.UnitHostPid.Value))
        {
            killer(record.UnitHostPid.Value);
            terminatedUnitHost = true;
        }

        var terminatedKernel = false;
        if (hasRecord && record.Pid > 0 && checkPid(record.Pid))
        {
            killer(record.Pid);
            terminatedKernel = true;
        }

        var cleaned = TryDeleteRunFile(layout.KernelPidFilePath);
        if (!string.IsNullOrWhiteSpace(layout.LegacyKernelPidFilePath))
        {
            cleaned = TryDeleteRunFile(layout.LegacyKernelPidFilePath) || cleaned;
        }

        cleaned = TryDeleteRunFile(layout.PresenceFilePath) || cleaned;
        cleaned = TryDeleteRunFile(KernelConfigFilePath(layout)) || cleaned;

        return new DesktopStopResult(
            GracefulShutdownAccepted: gracefulAccepted,
            TerminatedKernelPid: terminatedKernel,
            TerminatedUnitHostPid: terminatedUnitHost,
            CleanedRunFiles: cleaned);
    }

    private static async Task<bool> TryRequestGracefulShutdownAsync(
        int port,
        string presenceToken,
        HttpMessageHandler? customHandler,
        CancellationToken cancellationToken)
    {
        using var client = customHandler is not null
            ? new HttpClient(customHandler, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(1_500) }
            : new HttpClient { Timeout = TimeSpan.FromMilliseconds(1_500) };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/runtime/shutdown");
        request.Headers.TryAddWithoutValidation(CanonicalPresenceHeaderName, presenceToken);
        request.Headers.TryAddWithoutValidation(PresenceHeaderName, presenceToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presenceToken);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            Trace.TraceWarning("Graceful shutdown request failed on port {0}: {1}", port, ex.Message);
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static void KillProcessTreeById(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                _ = proc.WaitForExit(5_000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Trace.TraceWarning("Failed to terminate process {0}: {1}", pid, ex.Message);
        }
    }

    public static bool TryDeleteRunFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to delete run file '{0}': {1}", path, ex.Message);
        }

        return false;
    }
}

internal sealed record DesktopLayout(
    string HomeDirectory,
    string SecretsDirectory,
    string MasterKeyPath,
    string LegacyMasterKeyPath,
    string RunDirectory,
    string PresenceFilePath,
    string KernelPidFilePath,
    string StateDirectory,
    string LogsDirectory,
    string? LegacyKernelPidFilePath = null);

internal sealed record KernelPidRecord(
    int Pid,
    int Port,
    int? UnitHostPid = null,
    bool Background = false,
    string? StartedAt = null);

internal sealed record DesktopStatusSnapshot(
    string Status,
    bool Healthy,
    bool Listening,
    int? Pid,
    int? TrackedPid,
    int? UnitHostPid,
    int Port,
    bool Background,
    bool PresencePresent,
    bool StalePidFile,
    string HomeDirectory,
    double ProbeMs);

internal sealed record DesktopStopResult(
    bool GracefulShutdownAccepted,
    bool TerminatedKernelPid,
    bool TerminatedUnitHostPid,
    bool CleanedRunFiles);
