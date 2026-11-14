using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
            KernelPidFilePath: Path.Combine(root, "run", "kernel.json"),
            StateDirectory: Path.Combine(root, "state"),
            LogsDirectory: Path.Combine(root, "logs"));
    }

    public static int ResolvePort(Func<string, string?>? getEnv = null)
    {
        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        return int.TryParse(readEnv(DesktopPortEnvVar), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : DefaultPort;
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
            if (arg is "--headless" or "--no-tray" or "--verify-boot" or "--kernel-only" or "--quit" or "stop")
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

    public static string GeneratePresenceToken()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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

        if (!createNew && File.Exists(fullPath))
        {
            var existing = File.ReadAllText(fullPath, Encoding.UTF8).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
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

    public static void WriteKernelPidRecord(string pidFilePath, int pid, int port)
    {
        var directory = Path.GetDirectoryName(pidFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("pid", pid);
            writer.WriteNumber("port", port);
            writer.WriteString("startedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        File.WriteAllBytes(pidFilePath, buffer.ToArray());
        RestrictFileToCurrentUser(pidFilePath);
    }

    public static bool TryReadKernelPidRecord(string pidFilePath, out int pid, out int port)
    {
        pid = 0;
        port = 0;
        if (!File.Exists(pidFilePath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(pidFilePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("pid", out var pidProp)
                || !pidProp.TryGetInt32(out pid))
            {
                return false;
            }

            if (root.TryGetProperty("port", out var portProp))
            {
                _ = portProp.TryGetInt32(out port);
            }

            return pid > 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
    string LogsDirectory);
