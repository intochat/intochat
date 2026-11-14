using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Sandbox;

/// <summary>
/// Supervises an out-of-process <c>IntoChat.OS.Sandbox --unit-host</c> worker over anonymous JSON stdin/stdout pipes,
/// enforcing a hard per-turn watchdog timeout (default 5 seconds), Windows Job Object (<c>KILL_ON_JOB_CLOSE</c> + RAM cap),
/// <c>CreateNoWindow = true</c>, and a scrubbed environment with zero <c>MasterKey</c> or host API keys.
/// </summary>
public sealed class UnitHostRunner : IAsyncDisposable, IDisposable
{
    public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] SafeInheritedVariables =
    [
        "SystemRoot",
        "SystemDrive",
        "windir",
        "ComSpec",
        "PATHEXT",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramData",
        "DOTNET_ROOT",
        "DOTNET_MULTILEVEL_LOOKUP",
    ];

    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly string _workerAssemblyPath;
    private readonly string _workingDirectory;
    private readonly TimeSpan _turnTimeout;
    private readonly long _maxMemoryBytes;
    private readonly ILogger<UnitHostRunner> _logger;

    private WorkerSession? _session;
    private long _nextFrameId;
    private bool _disposed;

    public UnitHostRunner(
        string? workerAssemblyPath = null,
        string? workingDirectory = null,
        TimeSpan? turnTimeout = null,
        long maxMemoryBytes = 256L * 1024 * 1024,
        ILogger<UnitHostRunner>? logger = null)
    {
        _workerAssemblyPath = !string.IsNullOrWhiteSpace(workerAssemblyPath)
            ? Path.GetFullPath(workerAssemblyPath)
            : typeof(UnitHostRunner).Assembly.Location;
        _workingDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Path.Combine(Path.GetTempPath(), "intochat-unithost-" + Guid.NewGuid().ToString("N")[..12]);
        _turnTimeout = turnTimeout is { TotalMilliseconds: > 0 } configured ? configured : DefaultTurnTimeout;
        _maxMemoryBytes = maxMemoryBytes > 0 ? maxMemoryBytes : 256L * 1024 * 1024;
        _logger = logger ?? NullLogger<UnitHostRunner>.Instance;
    }

    public int? ActiveWorkerProcessId => _session is { Process.HasExited: false } active ? active.Process.Id : null;

    public static IReadOnlyDictionary<string, string> BuildSanitizedWorkerEnvironment(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var tempDir = Path.Combine(workingDirectory, "tmp");

        var sanitized = new SortedDictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var name in SafeInheritedVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } val)
            {
                sanitized[name] = val;
            }
        }

        // Preserve non-secret PATH entries so `dotnet` resolves cleanly, while never copying secret variables.
        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } ambientPath)
        {
            sanitized["PATH"] = ambientPath;
        }

        sanitized["TEMP"] = tempDir;
        sanitized["TMP"] = tempDir;
        sanitized["TMPDIR"] = tempDir;
        sanitized["HOME"] = workingDirectory;
        sanitized["USERPROFILE"] = workingDirectory;
        sanitized["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        sanitized["DOTNET_NOLOGO"] = "1";
        sanitized["SPIKE_WINDOWS"] = "0";

        // Defense-in-depth scrub: verify no IntoChatOS__*, *KEY*, *TOKEN*, *SECRET*, or *PASSWORD* ever survives.
        foreach (var key in sanitized.Keys.ToArray())
        {
            if (IsSecretVariableName(key))
            {
                sanitized.Remove(key);
            }
        }

        return sanitized;
    }

    public static bool IsSecretVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (name.StartsWith("IntoChatOS__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SANDBOX_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "INTOCHAT_PRESENCE_TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] blockedFragments = ["KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "CONNECTIONSTRING"];
        foreach (var fragment in blockedFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<int> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExecuteFrameAsync(
            new UnitHostFrameRequest(NextId(), "ping"),
            _turnTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.ProcessId ?? 0;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetWorkerEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExecuteFrameAsync(
            new UnitHostFrameRequest(NextId(), "env"),
            _turnTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.EnvironmentSnapshot ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<DeployAppResponse> DeployAsync(
        DeployAppRequest request,
        TimeSpan? timeoutOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await ExecuteFrameAsync(
            new UnitHostFrameRequest(NextId(), "deploy", Deploy: request),
            timeoutOverride ?? _turnTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Deploy ?? throw new InvalidOperationException(response.Error ?? "UnitHost deploy returned empty payload.");
    }

    public async Task<DispatchAppResponse> DispatchAsync(
        DispatchAppRequest request,
        TimeSpan? timeoutOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await ExecuteFrameAsync(
            new UnitHostFrameRequest(NextId(), "dispatch", Dispatch: request),
            timeoutOverride ?? _turnTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Dispatch ?? throw new InvalidOperationException(response.Error ?? "UnitHost dispatch returned empty payload.");
    }

    public async Task<UnloadAppResponse> UnloadAsync(
        string brainId,
        string appKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        var response = await ExecuteFrameAsync(
            new UnitHostFrameRequest(NextId(), "unload", Unload: new UnloadAppRequest(brainId, appKey)),
            _turnTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Unload ?? new UnloadAppResponse(Unloaded: false, brainId, appKey);
    }

    private async Task<UnitHostFrameResponse> ExecuteFrameAsync(
        UnitHostFrameRequest frame,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = EnsureSessionStarted();
            var wireJson = JsonSerializer.Serialize(frame, UnitHostJson.Options);

            using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdogCts.CancelAfter(timeout);

            string? replyLine;
            try
            {
                await session.Stdin.WriteLineAsync(wireJson.AsMemory(), watchdogCts.Token).ConfigureAwait(false);
                await session.Stdin.FlushAsync(watchdogCts.Token).ConfigureAwait(false);
                replyLine = await session.Stdout.ReadLineAsync(watchdogCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var killedPid = session.Process.Id;
                KillAndDisposeSession();
                _logger.LogWarning(
                    "UnitHost worker PID {WorkerPid} exceeded turn watchdog ({TimeoutMs}ms) on op '{Op}'; worker terminated.",
                    killedPid,
                    timeout.TotalMilliseconds,
                    frame.Op);
                throw new TimeoutException(
                    $"UnitHost turn '{frame.Op}' exceeded watchdog timeout of {timeout.TotalMilliseconds:F0}ms; worker PID {killedPid} was terminated.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                KillAndDisposeSession();
                throw;
            }

            if (string.IsNullOrWhiteSpace(replyLine))
            {
                var exitInfo = session.Process.HasExited ? $"exit code {session.Process.ExitCode}" : "closed stdout";
                KillAndDisposeSession();
                throw new InvalidOperationException($"UnitHost worker terminated unexpectedly during '{frame.Op}' ({exitInfo}).");
            }

            var response = JsonSerializer.Deserialize<UnitHostFrameResponse>(replyLine, UnitHostJson.Options)
                ?? throw new InvalidOperationException("UnitHost worker returned null JSON frame.");

            if (!response.Ok)
            {
                throw new InvalidOperationException(response.Error ?? $"UnitHost op '{frame.Op}' failed.");
            }

            return response;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private WorkerSession EnsureSessionStarted()
    {
        if (_session is { Process.HasExited: false } existing)
        {
            return existing;
        }

        KillAndDisposeSession();

        Directory.CreateDirectory(_workingDirectory);
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "tmp"));

        var dotnetExe = ResolveDotnetHostExecutable();
        var startInfo = new ProcessStartInfo(dotnetExe)
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(_workerAssemblyPath);
        startInfo.ArgumentList.Add("--unit-host");

        // Replace ambient environment completely with scrubbed environment.
        foreach (DictionaryEntry existingVar in Environment.GetEnvironmentVariables())
        {
            if (existingVar.Key is string key)
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment.Clear();
        foreach (var (k, v) in BuildSanitizedWorkerEnvironment(_workingDirectory))
        {
            startInfo.Environment[k] = v;
        }

        var jobHandle = OperatingSystem.IsWindows() ? CreateWindowsWorkerJobObject(_maxMemoryBytes) : IntPtr.Zero;
        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start UnitHost worker process.");

            if (jobHandle != IntPtr.Zero && OperatingSystem.IsWindows())
            {
                if (!JobNative.AssignProcessToJobObject(jobHandle, process.Handle) && !process.HasExited)
                {
                    _logger.LogDebug(
                        "AssignProcessToJobObject returned Win32 {Error} for UnitHost PID {Pid}; continuing with Process.Kill fallback.",
                        Marshal.GetLastWin32Error(),
                        process.Id);
                }
            }

            // Drain stderr asynchronously so stderr buffer fills never deadlock the worker.
            _ = DrainStderrAsync(process.StandardError);

            _session = new WorkerSession(process, process.StandardInput, process.StandardOutput, jobHandle);
            return _session;
        }
        catch
        {
            if (process is not null)
            {
                TryKillProcess(process);
                process.Dispose();
            }

            if (jobHandle != IntPtr.Zero)
            {
                _ = JobNative.TerminateJobObject(jobHandle, 1);
                _ = JobNative.CloseHandle(jobHandle);
            }

            throw;
        }
    }

    private static async Task DrainStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _ = ex.Message;
        }
    }

    private void KillAndDisposeSession()
    {
        var current = Interlocked.Exchange(ref _session, null);
        if (current is null)
        {
            return;
        }

        if (current.JobHandle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            _ = JobNative.TerminateJobObject(current.JobHandle, 1);
            _ = JobNative.CloseHandle(current.JobHandle);
        }

        TryKillProcess(current.Process);
        current.Process.Dispose();
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            _ = process.WaitForExit(3_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or SystemException)
        {
            _ = ex.Message;
        }
    }

    private static string ResolveDotnetHostExecutable()
    {
        if (Environment.ProcessPath is { Length: > 0 } currentExe)
        {
            var fileName = Path.GetFileNameWithoutExtension(currentExe);
            if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return currentExe;
            }
        }

        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
        {
            return hostPath;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private string NextId()
        => Interlocked.Increment(ref _nextFrameId).ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        KillAndDisposeSession();
        _turnLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static IntPtr CreateWindowsWorkerJobObject(long maxMemoryBytes)
    {
        var job = JobNative.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var size = Marshal.SizeOf<JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var limits = new JobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags =
                JobNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                | JobNative.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION
                | JobNative.JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                | JobNative.JOB_OBJECT_LIMIT_JOB_MEMORY;
            limits.BasicLimitInformation.ActiveProcessLimit = 4;
            limits.JobMemoryLimit = (UIntPtr)(ulong)maxMemoryBytes;

            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            _ = JobNative.SetInformationJobObject(job, JobNative.JobObjectExtendedLimitInformation, buffer, size);
            return job;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed record WorkerSession(
        Process Process,
        StreamWriter Stdin,
        StreamReader Stdout,
        IntPtr JobHandle);

    private static class JobNative
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
        public const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
        public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOps;
            public ulong WriteOps;
            public ulong OtherOps;
            public ulong ReadBytes;
            public ulong WriteBytes;
            public ulong OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
