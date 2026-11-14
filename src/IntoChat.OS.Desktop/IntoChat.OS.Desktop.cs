// The desktop entry point and supervisor: the plain single-machine host.
// Starts IntoChat.OS.Kernel and the Flutter desktop shell IN PARALLEL (<1.5s cold start),
// using a sub-millisecond in-memory socket check instead of a 2s closed-port HTTP probe,
// canonical home ~/.intochat (overridable via IntoChatOS__Home), DPAPI-protected master.key,
// atomic ~/.intochat/run/kernel.pid lifecycle tracking, and an owner-only per-boot presence token
// in ~/.intochat/run/presence passed via INTOCHAT_PRESENCE_TOKEN.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var packageRoot = AppContext.BaseDirectory;
var homeDir = DesktopBootstrap.ResolveHomeDirectory();
var layout = DesktopBootstrap.ResolveLayout(homeDir);
var port = DesktopBootstrap.ResolvePort();
var kernelUrl = new Uri($"http://127.0.0.1:{port}/");
var headless = DesktopBootstrap.IsHeadless(args);

Directory.CreateDirectory(layout.LogsDirectory);

if (args.Length == 2 && args[0] is "--export-key" or "--import-key")
{
    var effectiveKeyPath = File.Exists(layout.MasterKeyPath) || !File.Exists(layout.LegacyMasterKeyPath)
        ? layout.MasterKeyPath
        : layout.LegacyMasterKeyPath;
    return KeyBackupCommand.Run(args[0], Path.GetFullPath(args[1]), effectiveKeyPath, headless);
}

if (DesktopBootstrap.IsStatusCommand(args))
{
    TryAttachParentConsole();
    var snapshot = DesktopBootstrap.ProbeStatus(layout, kernelUrl.Port);
    Console.Out.WriteLine(DesktopBootstrap.FormatStatus(snapshot, DesktopBootstrap.IsTextFormatRequested(args)));
    return args.Contains("--check-healthy") && !snapshot.Healthy ? 1 : 0;
}

if (DesktopBootstrap.IsQuitCommand(args))
{
    TryAttachParentConsole();
    _ = await DesktopBootstrap.StopRunningKernelAsync(layout, kernelUrl.Port);
    return 0;
}

try
{
    if (args.Contains("--verify-boot"))
    {
        TryAttachParentConsole();
        return RunVerifyBoot(layout, kernelUrl);
    }

    var backgroundDaemon = DesktopBootstrap.IsBackgroundDaemon(args);
    var kernelOnly = args.Contains("--kernel-only") || backgroundDaemon;
    var keepKernelAliveOnClose = DesktopBootstrap.ShouldKeepKernelAliveOnWindowClose(args);

    var alreadyListening = DesktopBootstrap.IsPortListening(kernelUrl.Port);
    string presenceToken;
    Process? ownedKernel = null;

    if (!alreadyListening)
    {
        var masterKey = DesktopMasterKey.LoadOrCreate(layout);
        presenceToken = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: true);
        ownedKernel = StartKernel(
            Path.Combine(packageRoot, "os"),
            kernelUrl,
            layout,
            masterKey,
            presenceToken,
            Path.Combine(layout.LogsDirectory, "kernel.log"));
        DesktopBootstrap.WriteKernelPidRecord(
            layout.KernelPidFilePath,
            ownedKernel.Id,
            kernelUrl.Port,
            unitHostPid: null,
            background: keepKernelAliveOnClose || kernelOnly);
    }
    else
    {
        presenceToken = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: false);
    }

    // Start shell IMMEDIATELY in parallel with kernel boot when launching the desktop window.
    Process? shellProcess = null;
    var webMode = args.Contains("--web");

    if (!kernelOnly && !webMode)
    {
        var shellDir = Path.Combine(packageRoot, "shell");
        var shellStart = DesktopBootstrap.BuildShellStartInfo(shellDir, kernelUrl, presenceToken, createNoWindow: headless);
        if (File.Exists(shellStart.FileName))
        {
            shellProcess = Process.Start(shellStart) ?? throw new InvalidOperationException("Could not start the shell.");
        }
        else if (!headless)
        {
            webMode = true;
        }
    }

    // Verify kernel health concurrently while the Flutter shell initializes its graphics/engine in parallel.
    using (var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) })
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!await Healthy(http, kernelUrl))
        {
            if (ownedKernel is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The OS exited prematurely (code {ownedKernel.ExitCode}); see {Path.Combine(layout.LogsDirectory, "kernel.log")}.");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"The OS did not become healthy at {kernelUrl}; see {Path.Combine(layout.LogsDirectory, "kernel.log")}.");
            }

            await Task.Delay(50);
        }
    }

    if (webMode && !headless)
    {
        OpenBrowser($"{kernelUrl}#presence={presenceToken}");
    }

    if (kernelOnly)
    {
        return 0;
    }

    if (!keepKernelAliveOnClose)
    {
        // Clean lifecycle for --no-tray / ephemeral headless runs: wait for shell if running, and terminate owned kernel on exit.
        try
        {
            if (shellProcess is not null)
            {
                using (shellProcess)
                {
                    await shellProcess.WaitForExitAsync();
                    return shellProcess.ExitCode;
                }
            }

            return 0;
        }
        finally
        {
            if (ownedKernel is not null)
            {
                await TerminateOwnedKernelAsync(ownedKernel, layout, kernelUrl.Port);
            }
        }
    }

    // Hide-on-close / background tray daemon mode: closing the UI window leaves IntoChat.OS.Kernel alive for
    // [Every("1m")] micro-apps and pending PR approvals until `intochat --quit` / `--stop` is invoked.
    if (shellProcess is not null)
    {
        using (shellProcess)
        {
            await shellProcess.WaitForExitAsync();
            return shellProcess.ExitCode;
        }
    }

    return 0;
}
catch (Exception failure) when (failure is InvalidOperationException or IOException or CryptographicException or Win32Exception or UnauthorizedAccessException)
{
    await File.AppendAllTextAsync(
        Path.Combine(layout.LogsDirectory, "launcher.log"),
        $"{DateTimeOffset.Now:O} {failure}{Environment.NewLine}");

    if (!headless && OperatingSystem.IsWindows())
    {
        _ = NativeMethods.MessageBoxW(IntPtr.Zero, failure.Message, "IntoChat OS", 0x10);
    }
    else
    {
        TryAttachParentConsole();
        await Console.Error.WriteLineAsync(failure.Message);
    }

    return 1;
}

static void TryAttachParentConsole()
{
    if (OperatingSystem.IsWindows())
    {
        _ = NativeMethods.AttachConsole(-1);
    }
}

static int RunVerifyBoot(DesktopLayout layout, Uri kernelUrl)
{
    var sw = Stopwatch.StartNew();
    var listening = DesktopBootstrap.IsPortListening(kernelUrl.Port);
    var probeMs = sw.Elapsed.TotalMilliseconds;

    _ = DesktopMasterKey.LoadOrCreate(layout);
    var presenceToken = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: !listening);

    using var buffer = new MemoryStream();
    using (var json = new Utf8JsonWriter(buffer))
    {
        json.WriteStartObject();
        json.WriteString("status", "ok");
        json.WriteString("home", layout.HomeDirectory);
        json.WriteNumber("port", kernelUrl.Port);
        json.WriteBoolean("listening", listening);
        json.WriteNumber("portProbeMs", Math.Round(probeMs, 3));
        json.WriteBoolean("masterKeyPresent", File.Exists(layout.MasterKeyPath));
        json.WriteBoolean("presencePresent", File.Exists(layout.PresenceFilePath) && presenceToken.Length >= 32);
        json.WriteEndObject();
    }

    Console.Out.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    return 0;
}

static async Task TerminateOwnedKernelAsync(Process ownedKernel, DesktopLayout layout, int port)
{
    try
    {
        _ = await DesktopBootstrap.StopRunningKernelAsync(layout, port);
        if (!ownedKernel.HasExited)
        {
            ownedKernel.Kill(entireProcessTree: true);
            _ = ownedKernel.WaitForExit(5_000);
        }
    }
    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
    {
        _ = ex.Message;
    }
    finally
    {
        ownedKernel.Dispose();
        _ = DesktopBootstrap.TryDeleteRunFile(layout.KernelPidFilePath);
        if (!string.IsNullOrWhiteSpace(layout.LegacyKernelPidFilePath))
        {
            _ = DesktopBootstrap.TryDeleteRunFile(layout.LegacyKernelPidFilePath);
        }

        _ = DesktopBootstrap.TryDeleteRunFile(layout.PresenceFilePath);
    }
}

static void OpenBrowser(string url)
{
    try
    {
        _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
    {
        _ = ex.Message;
    }
}

static async Task<bool> Healthy(HttpClient http, Uri kernel)
{
    if (!DesktopBootstrap.IsPortListening(kernel.Port))
    {
        return false;
    }

    try
    {
        using var response = await http.GetAsync(new Uri(kernel, "health"));
        return response.IsSuccessStatusCode;
    }
    catch (HttpRequestException ex)
    {
        _ = ex.Message;
        return false;
    }
    catch (TaskCanceledException)
    {
        return false;
    }
}

static Process StartKernel(string osDirectory, Uri url, DesktopLayout layout, string masterKey, string presenceToken, string log)
{
    var start = DesktopBootstrap.BuildKernelStartInfo(osDirectory, url, layout, masterKey, presenceToken);
    var kernel = Process.Start(start) ?? throw new InvalidOperationException("Could not start the OS.");
    var writer = new StreamWriter(log, append: true) { AutoFlush = true };
    kernel.OutputDataReceived += (_, line) => Append(writer, line.Data);
    kernel.ErrorDataReceived += (_, line) => Append(writer, line.Data);
    kernel.BeginOutputReadLine();
    kernel.BeginErrorReadLine();
    return kernel;
}

static void Append(StreamWriter writer, string? text)
{
    if (text is null) { return; }
    lock (writer) { writer.WriteLine(text); }
}

internal static class DesktopMasterKey
{
    // Existing encrypted data keeps its original key: a missing key file next to a non-empty brain store is a stop, not a reset.
    public static string LoadOrCreate(DesktopLayout layout)
    {
        if (File.Exists(layout.MasterKeyPath))
        {
            return UnsealKeyFile(layout.MasterKeyPath);
        }

        if (File.Exists(layout.LegacyMasterKeyPath))
        {
            var migrated = UnsealKeyFile(layout.LegacyMasterKeyPath);
            SealKeyFile(layout.MasterKeyPath, migrated);
            return migrated;
        }

        if (Directory.Exists(layout.StateDirectory) && Directory.EnumerateFileSystemEntries(layout.StateDirectory).Any())
        {
            throw new InvalidOperationException(
                $"The brain store at {layout.StateDirectory} exists but its master key ({layout.MasterKeyPath}) is missing. Restore the key file; a new key would make the stored data unreadable.");
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        SealKeyFile(layout.MasterKeyPath, key);
        return key;
    }

    private static string UnsealKeyFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        return OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetString(Dpapi.Unprotect(raw))
            : Encoding.UTF8.GetString(raw).Trim();
    }

    private static void SealKeyFile(string path, string key)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        DesktopBootstrap.RestrictDirectoryToCurrentUser(dir);
        var payload = OperatingSystem.IsWindows()
            ? Dpapi.Protect(Encoding.UTF8.GetBytes(key))
            : Encoding.UTF8.GetBytes(key);
        File.WriteAllBytes(path, payload);
        DesktopBootstrap.RestrictFileToCurrentUser(path);
    }
}

// The launcher is a windowed app, so the backup commands borrow the calling terminal (or open one unless headless) for the passphrase.
internal static class KeyBackupCommand
{
    public static int Run(string command, string file, string keyPath, bool headless)
    {
        if (OperatingSystem.IsWindows() && !NativeMethods.AttachConsole(-1) && !headless)
        {
            _ = NativeMethods.AllocConsole();
        }

        try
        {
            if (command == "--export-key")
            {
                if (!File.Exists(keyPath)) { throw new InvalidOperationException($"There is no master key at {keyPath} yet; start IntoChat OS once first."); }
                if (File.Exists(file)) { throw new InvalidOperationException($"{file} already exists; choose a new file name."); }
                var passphrase = ReadPassphrase($"Passphrase for the backup (at least {DesktopKeyBackup.MinimumPassphraseLength} characters): ");
                if (ReadPassphrase("Repeat the passphrase: ") != passphrase) { throw new InvalidOperationException("The passphrases differ; nothing was written."); }
                var key = OperatingSystem.IsWindows()
                    ? Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(keyPath)))
                    : Encoding.UTF8.GetString(File.ReadAllBytes(keyPath)).Trim();
                File.WriteAllText(file, DesktopKeyBackup.Export(key, passphrase));
                Console.WriteLine($"Wrote the master key backup to {file}. Keep it and its passphrase somewhere other than this PC.");
            }
            else
            {
                DesktopKeyBackup.RequireNoKey(keyPath);
                var key = DesktopKeyBackup.Import(File.ReadAllText(file), ReadPassphrase("Backup passphrase: "));
                var dir = Path.GetDirectoryName(keyPath)!;
                Directory.CreateDirectory(dir);
                DesktopBootstrap.RestrictDirectoryToCurrentUser(dir);
                var sealedBytes = OperatingSystem.IsWindows()
                    ? Dpapi.Protect(Encoding.UTF8.GetBytes(key))
                    : Encoding.UTF8.GetBytes(key);
                File.WriteAllBytes(keyPath, sealedBytes);
                DesktopBootstrap.RestrictFileToCurrentUser(keyPath);
                Console.WriteLine($"Restored the master key to {keyPath}. Start IntoChat OS as usual.");
            }
            return 0;
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
    }

    private static string ReadPassphrase(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) { var line = Console.ReadLine() ?? ""; Console.WriteLine(); return line; }
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { break; }
            if (key.Key == ConsoleKey.Backspace) { if (typed.Length > 0) { typed.Length--; } continue; }
            if (!char.IsControl(key.KeyChar)) { typed.Append(key.KeyChar); }
        }
        Console.WriteLine();
        return typed.ToString();
    }
}

internal static unsafe class Dpapi
{
    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        {
            var source = new NativeMethods.DataBlob { Size = data.Length, Data = (IntPtr)input };
            var ok = protect
                ? NativeMethods.CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : NativeMethods.CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) { throw new CryptographicException(Marshal.GetLastPInvokeError()); }
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { _ = NativeMethods.LocalFree(output.Data); }
        }
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
