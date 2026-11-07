// The MSIX entry point: the plain single-machine host. The Aspire AppHost is a developer composition root with a dashboard,
// containers and generated parameters; an installed desktop has none of those. This launcher starts the same OS host
// (IntoChat.OS.Kernel, which composes the Windows edge on Windows) the way the AppHost's local profile configures it, on
// loopback, then opens the Flutter Windows shell against it. It owns what the AppHost owns in a developer run:
//   - the master key, generated once and kept DPAPI-protected for the current user; never regenerated over existing data;
//   - the brain store directory, under the user's local app data (the package's private copy when installed from MSIX);
//   - the shell's kernel URL (INTOCHATOS_UI_BASE);
//   - a passphrase backup of the master key: uninstalling deletes LocalState and the DPAPI copy with it, so
//     "intochat-os --export-key <file>" writes one and "intochat-os --import-key <file>" restores it before the next start.
// A second launch reuses a kernel that is already healthy on the port. The kernel keeps running after the shell closes:
// it is the OS, not a window, and a later launch attaches to it.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

var packageRoot = AppContext.BaseDirectory;
var dataRoot = Environment.GetEnvironmentVariable("INTOCHATOS_DESKTOP_DATA") is { Length: > 0 } configuredData
    ? configuredData
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntoChatOS", "Desktop");
var port = int.TryParse(Environment.GetEnvironmentVariable("INTOCHATOS_DESKTOP_PORT"), out var configuredPort) ? configuredPort : 5080;
var kernelUrl = new Uri($"http://127.0.0.1:{port}/");
// The package's data root is the OS's Home: the brain log lives in <Home>/state.
var brainStore = Path.Combine(dataRoot, "state");
var logs = Path.Combine(dataRoot, "logs");
Directory.CreateDirectory(logs);
var masterKeyPath = Path.Combine(dataRoot, "master-key.dpapi");

if (args.Length == 2 && args[0] is "--export-key" or "--import-key")
{ return KeyBackupCommand.Run(args[0], Path.GetFullPath(args[1]), masterKeyPath); }

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    if (!await Healthy(http, kernelUrl))
    {
        var masterKey = DesktopMasterKey.LoadOrCreate(masterKeyPath, brainStore);
        StartKernel(Path.Combine(packageRoot, "os"), kernelUrl, dataRoot, masterKey, Path.Combine(logs, "kernel.log"));
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!await Healthy(http, kernelUrl))
        {
            if (DateTime.UtcNow > deadline) { throw new InvalidOperationException($"The OS did not become healthy at {kernelUrl}; see {Path.Combine(logs, "kernel.log")}."); }
            await Task.Delay(500);
        }
    }
    if (args.Contains("--kernel-only")) { return 0; }
    var shell = new ProcessStartInfo(Path.Combine(packageRoot, "shell", "intochat_os_flutter.exe"))
    { UseShellExecute = false, WorkingDirectory = Path.Combine(packageRoot, "shell") };
    shell.Environment["INTOCHATOS_UI_BASE"] = kernelUrl.ToString();
    using var shellProcess = Process.Start(shell) ?? throw new InvalidOperationException("Could not start the shell.");
    await shellProcess.WaitForExitAsync();
    return shellProcess.ExitCode;
}
catch (Exception failure) when (failure is InvalidOperationException or IOException or CryptographicException or System.ComponentModel.Win32Exception)
{
    await File.AppendAllTextAsync(Path.Combine(logs, "launcher.log"), $"{DateTimeOffset.Now:O} {failure}{Environment.NewLine}");
    _ = NativeMethods.MessageBoxW(IntPtr.Zero, failure.Message, "IntoChat OS", 0x10);
    return 1;
}

static async Task<bool> Healthy(HttpClient http, Uri kernel)
{
    try
    {
        using var response = await http.GetAsync(new Uri(kernel, "health"));
        return response.IsSuccessStatusCode;
    }
    catch (HttpRequestException) { return false; }
    catch (TaskCanceledException) { return false; }
}

static void StartKernel(string osDirectory, Uri url, string home, string masterKey, string log)
{
    var start = new ProcessStartInfo(Path.Combine(osDirectory, "IntoChat.OS.Kernel.exe"))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = osDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    var environment = start.Environment;
    environment["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("INTOCHATOS_DESKTOP_ENVIRONMENT") is { Length: > 0 } named ? named : "Production";
    environment["ASPNETCORE_URLS"] = url.GetLeftPart(UriPartial.Authority);
    // A desktop install is secured: the shell signs in; nothing is served anonymously as the owner.
    environment["IntoChatOS__Auth__Posture"] = "Secured";
    environment["IntoChatOS__MasterKey"] = masterKey;
    environment["IntoChatOS__Home"] = home;
    environment["IntoChatOS__Topology"] = "local";
    environment["IntoChatOS__Modules__apps__Options__Features"] = Path.Combine(osDirectory, "features");
    // The kernel switches off the local topology's container-backed modules itself (IntoChatOSNames.LocalDisabledModules). An
    // installed desktop also leaves out these integrations; the Windows module stays on: it is the device connector this package ships.
    foreach (var module in (string[])["github", "time", "salesforce", "gmail"])
    { environment[$"IntoChatOS__Modules__{module}__Enabled"] = "false"; }
    var kernel = Process.Start(start) ?? throw new InvalidOperationException("Could not start the OS.");
    var writer = new StreamWriter(log, append: true) { AutoFlush = true };
    kernel.OutputDataReceived += (_, line) => Append(writer, line.Data);
    kernel.ErrorDataReceived += (_, line) => Append(writer, line.Data);
    kernel.BeginOutputReadLine();
    kernel.BeginErrorReadLine();
}

static void Append(StreamWriter writer, string? text)
{
    if (text is null) { return; }
    lock (writer) { writer.WriteLine(text); }
}

internal static class DesktopMasterKey
{
    // Existing encrypted data keeps its original key: a missing key file next to a non-empty brain store is a stop, not a reset.
    public static string LoadOrCreate(string path, string brainStore)
    {
        if (File.Exists(path)) { return System.Text.Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(path))); }
        if (Directory.Exists(brainStore) && Directory.EnumerateFileSystemEntries(brainStore).Any())
        { throw new InvalidOperationException($"The brain store at {brainStore} exists but its master key ({path}) is missing. Restore the key file; a new key would make the stored data unreadable."); }
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Dpapi.Protect(System.Text.Encoding.UTF8.GetBytes(key)));
        return key;
    }
}

// The launcher is a windowed app, so the backup commands borrow the calling terminal (or open one) for the passphrase.
internal static class KeyBackupCommand
{
    public static int Run(string command, string file, string keyPath)
    {
        if (!NativeMethods.AttachConsole(-1)) { _ = NativeMethods.AllocConsole(); }
        try
        {
            if (command == "--export-key")
            {
                if (!File.Exists(keyPath)) { throw new InvalidOperationException($"There is no master key at {keyPath} yet; start IntoChat OS once first."); }
                if (File.Exists(file)) { throw new InvalidOperationException($"{file} already exists; choose a new file name."); }
                var passphrase = ReadPassphrase($"Passphrase for the backup (at least {DesktopKeyBackup.MinimumPassphraseLength} characters): ");
                if (ReadPassphrase("Repeat the passphrase: ") != passphrase) { throw new InvalidOperationException("The passphrases differ; nothing was written."); }
                var key = System.Text.Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(keyPath)));
                File.WriteAllText(file, DesktopKeyBackup.Export(key, passphrase));
                Console.WriteLine($"Wrote the master key backup to {file}. Keep it and its passphrase somewhere other than this PC.");
            }
            else
            {
                DesktopKeyBackup.RequireNoKey(keyPath);
                var key = DesktopKeyBackup.Import(File.ReadAllText(file), ReadPassphrase("Backup passphrase: "));
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                File.WriteAllBytes(keyPath, Dpapi.Protect(System.Text.Encoding.UTF8.GetBytes(key)));
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
        var typed = new System.Text.StringBuilder();
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
