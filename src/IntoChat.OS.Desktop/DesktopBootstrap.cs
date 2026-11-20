using System.Buffers;
using System.Buffers.Text;
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
using IntoChat.OS.Host.Execution;

internal static partial class DesktopBootstrap
{
    public const int DefaultPort = 5080;

    public const string HomeEnvVar = "IntoChatOS__Home";
    public const string LegacyDesktopDataEnvVar = "INTOCHATOS_DESKTOP_DATA";
    public const string DesktopPortEnvVar = "INTOCHATOS_DESKTOP_PORT";
    public const string DesktopEnvironmentEnvVar = "INTOCHATOS_DESKTOP_ENVIRONMENT";
    public const string AspNetCoreEnvironmentEnvVar = "ASPNETCORE_ENVIRONMENT";
    public const string AspNetCoreUrlsEnvVar = "ASPNETCORE_URLS";
    public const string ConfigurationFileConfigKey = "IntoChatOS:ConfigurationFile";
    public const string MasterKeyEnvVar = "IntoChatOS__MasterKey";
    public const string TopologyEnvVar = "IntoChatOS__Topology";
    public const string ConfiguredCustodyAcknowledgedEnvVar = "IntoChatOS__Platform__Storage__ConfiguredCustodyAcknowledged";
    public const string AuthPostureEnvVar = "IntoChatOS__Auth__Posture";
    public const string AuthorityIdentityPostureEnvVar = "IntoChatOS__Authority__Identity__Posture";
    public const string PresenceTokenEnvVar = "INTOCHAT_PRESENCE_TOKEN";
    public const string PresenceTokenFileEnvVar = "IntoChatOS__Security__PresenceTokenFile";
    public const string PresenceTokenHashConfigKey = "IntoChatOS:Security:PresenceTokenHash";
    public const string PresenceHeaderName = "X-Presence-Token";
    public const string CanonicalPresenceHeaderName = "X-IntoChat-Presence";
    public const string ModulesDisabledEnvVar = "INTOCHAT_OS_MODULES_DISABLED";
    public const string OrleansDashboardEnabledEnvVar = "OrleansDashboard__Enabled";
    public const string IntoChatOrleansDashboardEnabledEnvVar = "IntoChatOS__OrleansDashboard__Enabled";
    public const string ShellUiBaseEnvVar = "INTOCHATOS_UI_BASE";
    public const string SpikeWindowsEnvVar = "SPIKE_WINDOWS";

    private static readonly string[] LegacyAppDataDirectoryNames = ["IntoChatOS", "IntoChat", "intochat"];
    private static readonly string[] LegacyMigratedFiles = ["machine-id"];
    private static readonly string[] LegacyMigratedDirectories = ["state", "behaviors", "brains", "channels", "identity", "config"];
    private static readonly HashSet<string> LegacyExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets",
        "deploy-tools",
        "test-credentials",
        "run",
        "logs",
        "secrets",
    };

    public static string KernelConfigFilePath(DesktopLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.RunDirectory, "kernel.config.json");
    }

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
            Trace.TraceWarning("PID check for {0} failed: {1}", pid, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Detects legacy desktop homes under <c>%LOCALAPPDATA%\IntoChat*</c> and copies master keys and state
    /// into <paramref name="layout"/> before a new master key is minted, without overwriting existing files
    /// and without copying install artifacts such as <c>deploy-tools</c> or <c>assets</c>.
    /// </summary>
    public static bool MigrateLegacyHome(DesktopLayout layout, string? localAppDataPath = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (File.Exists(layout.MasterKeyPath))
        {
            return false;
        }

        var appDataRoot = !string.IsNullOrWhiteSpace(localAppDataPath)
            ? localAppDataPath
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataRoot) || !Directory.Exists(appDataRoot))
        {
            return false;
        }

        var sourceDir = FindBestLegacyHomeDirectory(appDataRoot, layout.HomeDirectory);
        if (sourceDir is null)
        {
            return false;
        }

        var migratedAny = false;
        Directory.CreateDirectory(layout.HomeDirectory);
        RestrictDirectoryToCurrentUser(layout.HomeDirectory);
        Directory.CreateDirectory(layout.SecretsDirectory);
        RestrictDirectoryToCurrentUser(layout.SecretsDirectory);

        foreach (var candidateKey in (string[])
        [
            Path.Combine(sourceDir, "secrets", "master.key"),
            Path.Combine(sourceDir, "master.key"),
            Path.Combine(sourceDir, "master-key.dpapi"),
        ])
        {
            if (File.Exists(candidateKey))
            {
                if (string.Equals(Path.GetFileName(candidateKey), "master-key.dpapi", StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(layout.LegacyMasterKeyPath))
                {
                    File.Copy(candidateKey, layout.LegacyMasterKeyPath, overwrite: false);
                    RestrictFileToCurrentUser(layout.LegacyMasterKeyPath);
                }

                if (!File.Exists(layout.MasterKeyPath))
                {
                    File.Copy(candidateKey, layout.MasterKeyPath, overwrite: false);
                    RestrictFileToCurrentUser(layout.MasterKeyPath);
                }

                migratedAny = true;
                break;
            }
        }

        foreach (var fileName in LegacyMigratedFiles)
        {
            var srcFile = Path.Combine(sourceDir, fileName);
            var dstFile = Path.Combine(layout.HomeDirectory, fileName);
            if (File.Exists(srcFile) && !File.Exists(dstFile))
            {
                File.Copy(srcFile, dstFile, overwrite: false);
                migratedAny = true;
            }
        }

        foreach (var dirName in LegacyMigratedDirectories)
        {
            if (LegacyExcludedDirectories.Contains(dirName))
            {
                continue;
            }

            var srcSubDir = Path.Combine(sourceDir, dirName);
            if (!Directory.Exists(srcSubDir))
            {
                continue;
            }

            var dstSubDir = Path.Combine(layout.HomeDirectory, dirName);
            if (CopyDirectoryWithoutOverwrite(srcSubDir, dstSubDir))
            {
                migratedAny = true;
            }
        }

        return migratedAny;
    }

    /// <summary>
    /// Ensures a master key file exists at <paramref name="masterKeyPath"/>, migrating any legacy home first
    /// before minting and sealing a fresh key.
    /// </summary>
    public static byte[] EnsureMasterKey(
        string masterKeyPath,
        string legacyMasterKeyPath,
        Func<string> generateKey,
        Func<byte[], byte[]> seal,
        string? localAppDataPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyMasterKeyPath);
        ArgumentNullException.ThrowIfNull(generateKey);
        ArgumentNullException.ThrowIfNull(seal);

        var secretsDir = Path.GetDirectoryName(masterKeyPath);
        var homeDir = !string.IsNullOrWhiteSpace(secretsDir) && string.Equals(Path.GetFileName(secretsDir), "secrets", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(secretsDir)
            : secretsDir;
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            var layout = ResolveLayout(homeDir);
            MigrateLegacyHome(layout, localAppDataPath);
        }

        if (File.Exists(masterKeyPath))
        {
            return File.ReadAllBytes(masterKeyPath);
        }

        if (File.Exists(legacyMasterKeyPath))
        {
            var migratedBytes = File.ReadAllBytes(legacyMasterKeyPath);
            WriteSealedKeyBytes(masterKeyPath, migratedBytes);
            return migratedBytes;
        }

        var freshRaw = Encoding.UTF8.GetBytes(generateKey());
        var sealedBytes = seal(freshRaw);
        WriteSealedKeyBytes(masterKeyPath, sealedBytes);
        return sealedBytes;
    }

    private static string? FindBestLegacyHomeDirectory(string appDataRoot, string targetHomeDirectory)
    {
        var fullTarget = Path.GetFullPath(targetHomeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedCandidates = new List<string>();

        foreach (var preferred in LegacyAppDataDirectoryNames)
        {
            var candidate = Path.Combine(appDataRoot, preferred);
            if (seen.Add(candidate))
            {
                orderedCandidates.Add(candidate);
            }
        }

        foreach (var wildcard in Directory.GetDirectories(appDataRoot, "IntoChat*", SearchOption.TopDirectoryOnly))
        {
            if (seen.Add(wildcard))
            {
                orderedCandidates.Add(wildcard);
            }
        }

        string? fallbackWithState = null;
        foreach (var candidate in orderedCandidates)
        {
            var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullCandidate, fullTarget, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fullCandidate))
            {
                continue;
            }

            if (File.Exists(Path.Combine(fullCandidate, "secrets", "master.key"))
                || File.Exists(Path.Combine(fullCandidate, "master.key"))
                || File.Exists(Path.Combine(fullCandidate, "master-key.dpapi")))
            {
                return fullCandidate;
            }

            if (fallbackWithState is null && LegacyMigratedDirectories.Any(d => Directory.Exists(Path.Combine(fullCandidate, d)) && Directory.EnumerateFileSystemEntries(Path.Combine(fullCandidate, d)).Any()))
            {
                fallbackWithState = fullCandidate;
            }
        }

        return fallbackWithState;
    }

    private static bool CopyDirectoryWithoutOverwrite(string sourceDir, string targetDir)
    {
        var copiedAny = false;
        Directory.CreateDirectory(targetDir);
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(filePath));
            if (!File.Exists(destFile))
            {
                File.Copy(filePath, destFile, overwrite: false);
                copiedAny = true;
            }
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(subDir);
            if (LegacyExcludedDirectories.Contains(name))
            {
                continue;
            }

            if (CopyDirectoryWithoutOverwrite(subDir, Path.Combine(targetDir, name)))
            {
                copiedAny = true;
            }
        }

        return copiedAny;
    }

    private static void WriteSealedKeyBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            RestrictDirectoryToCurrentUser(dir);
        }

        var temp = path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        File.WriteAllBytes(temp, bytes);
        RestrictFileToCurrentUser(temp);
        File.Move(temp, path, overwrite: true);
        RestrictFileToCurrentUser(path);
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
            Trace.TraceWarning("Failed to read presence file '{0}': {1}", presenceFilePath, ex.Message);
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
        ChildEnvironmentPolicy.Scrub(environment);
        var configPath = WriteKernelConfigFile(layout, masterKey, presenceToken);
        start.ArgumentList.Add($"--{ConfigurationFileConfigKey}={configPath}");

        environment[AspNetCoreEnvironmentEnvVar] = readEnv(DesktopEnvironmentEnvVar) is { Length: > 0 } named
            ? named
            : "Production";
        environment[AspNetCoreUrlsEnvVar] = kernelUrl.GetLeftPart(UriPartial.Authority);
        environment[HomeEnvVar] = layout.HomeDirectory;
        environment[TopologyEnvVar] = "local";
        environment[ConfiguredCustodyAcknowledgedEnvVar] = "true";
        environment[AuthPostureEnvVar] = "Open";
        environment[AuthorityIdentityPostureEnvVar] = "Open";
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

    internal static string WriteKernelConfigFile(DesktopLayout layout, string masterKey, string presenceToken)
    {
        Directory.CreateDirectory(layout.RunDirectory);
        RestrictDirectoryToCurrentUser(layout.RunDirectory);
        var configPath = KernelConfigFilePath(layout);
        var tempPath = configPath + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var tokenHash = ComputePresenceTokenHash(presenceToken);

        using (var buffer = new MemoryStream())
        {
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteStartObject("IntoChatOS");
                json.WriteString("MasterKey", masterKey);
                json.WriteStartObject("Security");
                json.WriteString("PresenceTokenFile", layout.PresenceFilePath);
                json.WriteString("PresenceTokenHash", tokenHash);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
            }

            File.WriteAllBytes(tempPath, buffer.ToArray());
        }

        RestrictFileToCurrentUser(tempPath);
        File.Move(tempPath, configPath, overwrite: true);
        RestrictFileToCurrentUser(configPath);
        return configPath;
    }

    internal static string ComputePresenceTokenHash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var trimmed = token.AsSpan().Trim();
        Span<byte> secret = stackalloc byte[512];

        if (trimmed.Length >= 64 && trimmed.Length % 2 == 0 && IsHex(trimmed)
            && Convert.FromHexString(trimmed, secret, out _, out var secretLen) == OperationStatus.Done
            && secretLen >= 32)
        {
            return HashLowerHex(secret[..secretLen]);
        }

        if (Base64Url.IsValid(trimmed)
            && Base64Url.TryDecodeFromChars(trimmed, secret, out secretLen)
            && secretLen >= 32)
        {
            return HashLowerHex(secret[..secretLen]);
        }

        if (Convert.TryFromBase64Chars(trimmed, secret, out secretLen) && secretLen >= 32)
        {
            return HashLowerHex(secret[..secretLen]);
        }

        var utf8 = Encoding.UTF8.GetBytes(trimmed.ToString());
        return HashLowerHex(utf8);
    }

    private static string HashLowerHex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexStringLower(digest);
    }

    private static bool IsHex(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
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
        ChildEnvironmentPolicy.Scrub(start.Environment);
        start.Environment[ShellUiBaseEnvVar] = kernelUrl.ToString();
        start.ArgumentList.Add($"--presence-token={presenceToken}");
        return start;
    }
}
