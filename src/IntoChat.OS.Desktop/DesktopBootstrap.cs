using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static class DesktopBootstrap
{
    public const int DefaultPort = 5080;

    public const string HomeEnvVar = "IntoChatOS__Home";
    public const string LegacyDesktopDataEnvVar = "INTOCHATOS_DESKTOP_DATA";
    public const string DesktopPortEnvVar = "INTOCHATOS_DESKTOP_PORT";
    public const string DesktopEnvironmentEnvVar = "INTOCHATOS_DESKTOP_ENVIRONMENT";
    public const string AspNetCoreEnvironmentEnvVar = "ASPNETCORE_ENVIRONMENT";
    public const string AspNetCoreUrlsEnvVar = "ASPNETCORE_URLS";
    public const string MasterKeyEnvVar = "IntoChatOS__MasterKey";
    public const string TopologyEnvVar = "IntoChatOS__Topology";
    public const string ConfiguredCustodyAcknowledgedEnvVar = "IntoChatOS__Platform__Storage__ConfiguredCustodyAcknowledged";
    public const string AuthPostureEnvVar = "IntoChatOS__Auth__Posture";
    public const string AuthorityIdentityPostureEnvVar = "IntoChatOS__Authority__Identity__Posture";
    public const string PresenceTokenEnvVar = "INTOCHAT_PRESENCE_TOKEN";
    public const string PresenceTokenFileEnvVar = "IntoChatOS__Security__PresenceTokenFile";
    public const string PresenceHeaderName = "X-Presence-Token";
    public const string ModulesDisabledEnvVar = "INTOCHAT_OS_MODULES_DISABLED";
    public const string OrleansDashboardEnabledEnvVar = "OrleansDashboard__Enabled";
    public const string IntoChatOrleansDashboardEnabledEnvVar = "IntoChatOS__OrleansDashboard__Enabled";
    public const string ShellUiBaseEnvVar = "INTOCHATOS_UI_BASE";
    public const string SpikeWindowsEnvVar = "SPIKE_WINDOWS";

    public static readonly string[] DefaultDisabledDesktopModules = ["salesforce", "gmail"];

    public static string ModuleEnabledEnvVar(string moduleId)
        => $"IntoChatOS__Modules__{moduleId}__Enabled";

    public static string ResolveHomeDirectory(Func<string, string?>? getEnv = null, string? userProfileOverride = null)
    {
        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        if (readEnv(HomeEnvVar) is { Length: > 0 } configuredHome && !string.IsNullOrWhiteSpace(configuredHome))
        {
            return Path.GetFullPath(configuredHome.Trim());
        }

        if (readEnv(LegacyDesktopDataEnvVar) is { Length: > 0 } legacyHome && !string.IsNullOrWhiteSpace(legacyHome))
        {
            return Path.GetFullPath(legacyHome.Trim());
        }

        var profile = !string.IsNullOrWhiteSpace(userProfileOverride)
            ? userProfileOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".intochat");
    }

    public static DesktopLayout ResolveLayout(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        var root = Path.GetFullPath(homeDirectory);
        return new DesktopLayout(
            HomeDirectory: root,
            SecretsDirectory: Path.Combine(root, "secrets"),
            MasterKeyPath: Path.Combine(root, "secrets", "master.key"),
            LegacyMasterKeyPath: Path.Combine(root, "master-key.dpapi"),
            RunDirectory: Path.Combine(root, "run"),
            PresenceFilePath: Path.Combine(root, "run", "presence"),
            KernelPidFilePath: Path.Combine(root, "run", "kernel.pid"),
            StateDirectory: Path.Combine(root, "state"),
            LogsDirectory: Path.Combine(root, "logs"),
            LegacyKernelPidFilePath: Path.Combine(root, "run", "kernel.json"));
    }

    public static int ResolvePort(Func<string, string?>? getEnv = null)
    {
        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        return int.TryParse(readEnv(DesktopPortEnvVar), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : DefaultPort;
    }

    public static bool IsStatusCommand(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (arg is "--status" or "status")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTextFormatRequested(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (arg is "--text" or "--format=text")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsQuitCommand(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (arg is "--quit" or "--stop" or "stop")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsBackgroundDaemon(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (arg is "--background" or "--daemon")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether closing the desktop UI shell window should leave <c>IntoChat.OS.Kernel</c> running in the
    /// background (for <c>[Every("1m")]</c> scheduled micro-apps and pending PR approvals) until <c>--quit</c> is invoked.
    /// </summary>
    public static bool ShouldKeepKernelAliveOnWindowClose(ReadOnlySpan<string> args, Func<string, string?>? getEnv = null)
    {
        var explicitHideOnClose = false;
        var explicitNoTray = false;

        foreach (var arg in args)
        {
            if (arg is "--no-tray")
            {
                explicitNoTray = true;
            }
            else if (arg is "--background" or "--daemon" or "--hide-on-close")
            {
                explicitHideOnClose = true;
            }
        }

        if (explicitNoTray)
        {
            return false;
        }

        if (explicitHideOnClose)
        {
            return true;
        }

        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        if (string.Equals(readEnv(SpikeWindowsEnvVar), "0", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var arg in args)
        {
            if (arg is "--headless")
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHeadless(ReadOnlySpan<string> args, Func<string, string?>? getEnv = null)
    {
        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        if (string.Equals(readEnv(SpikeWindowsEnvVar), "0", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var arg in args)
        {
            if (arg is "--headless" or "--no-tray" or "--verify-boot" or "--kernel-only"
                or "--status" or "status" or "--quit" or "--stop" or "stop"
                or "--background" or "--daemon")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fast in-memory OS socket table check (&lt;0.5ms on Windows) replacing the 2,043ms closed-port HTTP SYN timeout.
    /// </summary>
    public static bool IsPortListening(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(ep => ep.Port == port);
    }

    public static int[] GetBoundTcpPorts()
        => IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(static ep => ep.Port)
            .Distinct()
            .ToArray();

    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            _ = ex.Message;
            return false;
        }
    }

    public static string GeneratePresenceToken()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string? ReadPresenceToken(string presenceFilePath)
    {
        if (string.IsNullOrWhiteSpace(presenceFilePath) || !File.Exists(presenceFilePath))
        {
            return null;
        }

        try
        {
            var token = File.ReadAllText(presenceFilePath, Encoding.UTF8).Trim();
            return token.Length >= 32 ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Writes a fresh 256-bit presence token to <paramref name="presenceFilePath"/> with Owner-only ACLs,
    /// or reuses an existing valid token when attaching to an already-running kernel (`createNew: false`).
    /// </summary>
    public static string EnsurePresenceToken(string presenceFilePath, bool createNew = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presenceFilePath);
        var fullPath = Path.GetFullPath(presenceFilePath);
        var runDir = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Presence path '{fullPath}' has no parent directory.");

        Directory.CreateDirectory(runDir);
        RestrictDirectoryToCurrentUser(runDir);

        if (!createNew && ReadPresenceToken(fullPath) is { } existing)
        {
            return existing;
        }

        var token = GeneratePresenceToken();
        var tempPath = fullPath + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(tempPath, token, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictFileToCurrentUser(tempPath);
        File.Move(tempPath, fullPath, overwrite: true);
        RestrictFileToCurrentUser(fullPath);
        return token;
    }

    public static void RestrictDirectoryToCurrentUser(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var ownerSid = identity.User;
            if (ownerSid is null)
            {
                return;
            }

            var directoryInfo = new DirectoryInfo(directoryPath);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                ownerSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directoryInfo.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void RestrictFileToCurrentUser(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var ownerSid = identity.User;
            if (ownerSid is null)
            {
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                ownerSid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static ProcessStartInfo BuildKernelStartInfo(
        string osDirectory,
        Uri kernelUrl,
        DesktopLayout layout,
        string masterKey,
        string presenceToken,
        Func<string, string?>? getEnv = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(osDirectory);
        ArgumentNullException.ThrowIfNull(kernelUrl);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(presenceToken);

        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        var exeName = OperatingSystem.IsWindows() ? "IntoChat.OS.Kernel.exe" : "IntoChat.OS.Kernel";
        var start = new ProcessStartInfo(Path.Combine(osDirectory, exeName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = osDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var environment = start.Environment;
        environment[AspNetCoreEnvironmentEnvVar] = readEnv(DesktopEnvironmentEnvVar) is { Length: > 0 } named
            ? named
            : "Production";
        environment[AspNetCoreUrlsEnvVar] = kernelUrl.GetLeftPart(UriPartial.Authority);
        environment[HomeEnvVar] = layout.HomeDirectory;
        environment[TopologyEnvVar] = "local";
        environment[MasterKeyEnvVar] = masterKey;
        environment[ConfiguredCustodyAcknowledgedEnvVar] = "true";
        environment[AuthPostureEnvVar] = "Open";
        environment[AuthorityIdentityPostureEnvVar] = "Open";
        environment[PresenceTokenEnvVar] = presenceToken;
        environment[PresenceTokenFileEnvVar] = layout.PresenceFilePath;
        environment[OrleansDashboardEnabledEnvVar] = "false";
        environment[IntoChatOrleansDashboardEnabledEnvVar] = "false";

        var disabledCsv = readEnv(ModulesDisabledEnvVar) is { Length: > 0 } configuredDisabled
            ? configuredDisabled
            : string.Join(',', DefaultDisabledDesktopModules);
        environment[ModulesDisabledEnvVar] = disabledCsv;

        // Clear any ambient inherited disablement on github or time unless explicitly listed in disabledCsv.
        var disabledModules = disabledCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in (string[])["github", "time", "salesforce", "gmail"])
        {
            var key = ModuleEnabledEnvVar(candidate);
            if (disabledModules.Contains(candidate))
            {
                environment[key] = "false";
            }
            else
            {
                environment.Remove(key);
            }
        }

        return start;
    }

    public static ProcessStartInfo BuildShellStartInfo(
        string shellDirectory,
        Uri kernelUrl,
        string presenceToken,
        bool createNoWindow = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellDirectory);
        ArgumentNullException.ThrowIfNull(kernelUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(presenceToken);

        var exeName = OperatingSystem.IsWindows() ? "intochat_os_flutter.exe" : "intochat_os_flutter";
        var start = new ProcessStartInfo(Path.Combine(shellDirectory, exeName))
        {
            UseShellExecute = false,
            CreateNoWindow = createNoWindow,
            WorkingDirectory = shellDirectory,
        };
        start.Environment[ShellUiBaseEnvVar] = kernelUrl.ToString();
        start.Environment[PresenceTokenEnvVar] = presenceToken;
        return start;
    }

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

            using var doc = JsonDocument.Parse(utf8Span.ToArray());
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
            _ = ex.Message;
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
        request.Headers.TryAddWithoutValidation(PresenceHeaderName, presenceToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presenceToken);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            _ = ex.Message;
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
            _ = ex.Message;
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
            _ = ex.Message;
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
