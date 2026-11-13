using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace IntoChat.OS.Kernel.Composition.Operations;

internal sealed class SystemLocalProcessProbe(ILogger<SystemLocalProcessProbe>? logger = null) : ILocalProcessProbe
{
    private readonly ILogger<SystemLocalProcessProbe> _logger = logger ?? NullLogger<SystemLocalProcessProbe>.Instance;

    public int CurrentProcessId => Environment.ProcessId;

    // Native Windows is the supported proof boundary. Windows containers virtualize process IDs; the registry
    // discriminator follows dotnet/runtime's PlatformDetection. Linux/macOS retain membership-only recovery.
    public string? Namespace
    {
        get
        {
            if (!OperatingSystem.IsWindows() || ContainerFlag("DOTNET_RUNNING_IN_CONTAINER") || ContainerFlag("DOTNET_RUNNING_IN_CONTAINERS"))
            { return null; }
            try
            {
                using var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control", writable: false);
                return control is not null && control.GetValue("ContainerType") is null ? "windows-host" : null;
            }
            catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug("Local process namespace observation is unavailable ({ExceptionType}).", error.GetType().Name);
                return null;
            }
        }
    }

    public ProcessObservation Observe(int processId)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return new(false); }
        using (process)
        {
            try
            {
                if (process.HasExited) { return new(false); }
                var started = process.StartTime.ToUniversalTime().Ticks;
                return process.HasExited ? new(false) : new(true, started);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _logger.LogDebug("Local process incarnation observation is unavailable ({ExceptionType}).", error.GetType().Name);
                return default;
            }
        }
    }

    private static bool ContainerFlag(string name)
        => Environment.GetEnvironmentVariable(name) is { } value && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
