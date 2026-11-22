using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using DigitalBrain.Log;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using Microsoft.Win32;

namespace IntoChat.OS.Modules.Devices;

// Probes this machine's RAM, VRAM, and accelerators once per process without WMI (AOT-safe).
internal sealed class HardwareProbe : IHardwareProbe
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;
    private const string DisplayAdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly Lazy<Hardware> _cached = new(ProbeOnce, LazyThreadSafetyMode.ExecutionAndPublication);
    private int _npuDetected;

    public Hardware Read()
    {
        var hw = _cached.Value;
        if (Volatile.Read(ref _npuDetected) == 0 || hw.Accelerators.Contains(Accelerator.Npu))
        {
            return hw;
        }
        return hw with { Accelerators = [.. hw.Accelerators, Accelerator.Npu] };
    }

    public void ReportNpu() => Interlocked.Exchange(ref _npuDetected, 1);

    internal static long ParseNvidiaSmi(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        long totalMiB = 0;
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawLine.Split(',', 2, StringSplitOptions.TrimEntries)[0];
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) && mib > 0)
            {
                totalMiB += mib;
            }
        }
        return totalMiB * MiB;
    }

    private static Hardware ProbeOnce()
    {
        var ramBytes = Math.Max(0L, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var accelerators = new List<Accelerator> { Accelerator.Cpu };
        long vramBytes = 0;

        if (OperatingSystem.IsWindows())
        {
            vramBytes = ProbeWindowsVram();
            if (HasWindowsCuda())
            {
                accelerators.Add(Accelerator.Cuda);
            }
            if (vramBytes >= GiB)
            {
                accelerators.Add(Accelerator.DirectML);
            }
        }
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            vramBytes = ramBytes;
            accelerators.Add(Accelerator.Metal);
        }
        else if (OperatingSystem.IsLinux())
        {
            var nvidiaBytes = ProbeLinuxNvidiaSmi();
            if (nvidiaBytes > 0)
            {
                vramBytes = nvidiaBytes;
                accelerators.Add(Accelerator.Cuda);
            }
            else
            {
                vramBytes = ProbeLinuxDrmVram();
            }
        }

        return new Hardware(ramBytes, vramBytes, [.. accelerators]);
    }

    [SupportedOSPlatform("windows")]
    private static long ProbeWindowsVram()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey, writable: false);
            if (classKey is null)
            {
                return 0;
            }

            long maxVram = 0;
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit))
                {
                    continue;
                }

                try
                {
                    using var adapterKey = classKey.OpenSubKey(subKeyName, writable: false);
                    var value = adapterKey?.GetValue("HardwareInformation.qwMemorySize");
                    var bytes = value switch
                    {
                        long qword when qword > 0 => qword,
                        ulong uqword when uqword <= long.MaxValue => (long)uqword,
                        int dword when dword > 0 => dword,
                        uint udword => udword,
                        byte[] raw when raw.Length >= 8 => BitConverter.ToInt64(raw, 0),
                        _ => 0L,
                    };
                    if (bytes > maxVram)
                    {
                        maxVram = bytes;
                    }
                }
                catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
                {
                    OsConsoleLog.For<HardwareProbe>().LogSwallowed(error, $"reading display adapter registry key {subKeyName}");
                }
            }
            return maxVram;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            OsConsoleLog.For<HardwareProbe>().LogSwallowed(error, "reading Windows display adapter class registry key");
            return 0;
        }
    }

    private static bool HasWindowsCuda()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var handle))
        {
            return false;
        }
        NativeLibrary.Free(handle);
        return true;
    }

    private static long ProbeLinuxNvidiaSmi()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            ChildEnvironmentPolicy.Scrub(start);
            using var process = Process.Start(start);
            if (process is null)
            {
                return 0;
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1_500))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception killError) when (killError is InvalidOperationException or NotSupportedException)
                {
                    OsConsoleLog.For<HardwareProbe>().LogSwallowed(killError, "stopping timed-out nvidia-smi probe");
                }
                return 0;
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? ParseNvidiaSmi(stdout) : 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            OsConsoleLog.For<HardwareProbe>().LogSwallowed(error, "probing nvidia-smi for Linux GPU memory");
            return 0;
        }
    }

    private static long ProbeLinuxDrmVram()
    {
        const string drmRoot = "/sys/class/drm";
        if (!Directory.Exists(drmRoot))
        {
            return 0;
        }

        long maxVram = 0;
        try
        {
            foreach (var cardDir in Directory.EnumerateDirectories(drmRoot, "card*"))
            {
                var path = Path.Combine(cardDir, "device", "mem_info_vram_total");
                if (!File.Exists(path))
                {
                    continue;
                }
                if (long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > maxVram)
                {
                    maxVram = bytes;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            OsConsoleLog.For<HardwareProbe>().LogSwallowed(error, "reading Linux DRM VRAM sysfs files");
        }
        return maxVram;
    }
}
