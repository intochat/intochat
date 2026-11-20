using System.Buffers.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using IntoChat.OS.Authority.Identity;

namespace IntoChat.OS.Tests.Support;

/// <summary>
/// Isolated temporary IntoChat OS home directory with owner-only key custody and cleanup.
/// Never touches the user's profile home directory.
/// </summary>
public sealed class TestHome : IDisposable
{
    public string Path { get; }

    public string RunDirectory => System.IO.Path.Combine(Path, "run");

    public string PresenceFilePath => System.IO.Path.Combine(RunDirectory, "presence");

    public string SecretsDirectory => System.IO.Path.Combine(Path, "secrets");

    public string MasterKeyPath => System.IO.Path.Combine(SecretsDirectory, "master.key");

    public string KernelConfigFilePath => System.IO.Path.Combine(RunDirectory, "kernel.config.json");

    public string StateDirectory => System.IO.Path.Combine(Path, "state");

    public TestHome(string prefix = "intochat-os-test-")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string LoadOrCreateSealedMasterKey()
    {
        if (File.Exists(MasterKeyPath))
        {
            return UnsealMasterKeyFile(MasterKeyPath);
        }

        if (Directory.Exists(StateDirectory) && Directory.EnumerateFileSystemEntries(StateDirectory).Any())
        {
            throw new InvalidOperationException(
                $"The brain store at '{StateDirectory}' exists without '{MasterKeyPath}'.");
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(SecretsDirectory);
        RestrictDirectoryToCurrentUser(SecretsDirectory);

        var payload = OperatingSystem.IsWindows()
            ? Dpapi.Protect(Encoding.UTF8.GetBytes(key))
            : Encoding.UTF8.GetBytes(key);
        var temp = MasterKeyPath + ".tmp." + Environment.ProcessId;
        File.WriteAllBytes(temp, payload);
        RestrictFileToCurrentUser(temp);
        File.Move(temp, MasterKeyPath, overwrite: true);
        RestrictFileToCurrentUser(MasterKeyPath);
        return key;
    }

    public string ReadSealedMasterKey()
    {
        if (!File.Exists(MasterKeyPath))
        {
            throw new FileNotFoundException($"Expected sealed master key at '{MasterKeyPath}'.", MasterKeyPath);
        }

        return UnsealMasterKeyFile(MasterKeyPath);
    }

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root containing Intochat.OS.slnx was not found.");
    }

    public static async Task<string> ReadPresenceTokenAsync(string presenceFilePath, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presenceFilePath);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow.Add(effectiveTimeout);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(presenceFilePath))
            {
                var bytes = await File.ReadAllBytesAsync(presenceFilePath, ct).ConfigureAwait(false);
                if (bytes.Length >= PresenceProof.SecretByteCount)
                {
                    var isPrintableAscii = bytes.All(static b => b is (byte)'\r' or (byte)'\n' or (byte)'\t' || b is >= 0x21 and <= 0x7E);
                    return isPrintableAscii
                        ? Encoding.UTF8.GetString(bytes).Trim()
                        : Base64Url.EncodeToString(bytes);
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Kernel did not mint presence proof at '{presenceFilePath}' within {effectiveTimeout.TotalSeconds:0}s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        }
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

    public void Dispose()
    {
        DeleteDirectoryIfPresent(Path);
    }

    public static void DeleteDirectoryIfPresent(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static string UnsealMasterKeyFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        return OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetString(Dpapi.Unprotect(raw))
            : Encoding.UTF8.GetString(raw).Trim();
    }

    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] data) => Run(data, protect: true);

        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
            }

            var inputPtr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, inputPtr, data.Length);
                var source = new DataBlob { Size = data.Length, Data = inputPtr };
                var ok = protect
                    ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok)
                {
                    throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    _ = LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            IntPtr entropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            IntPtr entropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
