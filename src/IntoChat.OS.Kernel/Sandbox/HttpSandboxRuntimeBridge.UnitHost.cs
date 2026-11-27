using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using IntoChat.OS.Host.Execution;

namespace IntoChat.OS.Kernel.Sandbox;

internal sealed partial class HttpSandboxRuntimeBridge
{
    private sealed class UnitHostChildSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly WindowsJobObject? _job;
        private readonly Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> _callbackHandler;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<UnitHostFrameResponse>> _pending = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private int _disposed;

        private UnitHostChildSession(
            Process process,
            WindowsJobObject? job,
            Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> callbackHandler,
            ILogger logger)
        {
            _process = process;
            _job = job;
            _callbackHandler = callbackHandler;
            _logger = logger;
            _stdoutTask = Task.Run(() => ReadStdoutLoopAsync(_cts.Token));
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_cts.Token));
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public static UnitHostChildSession Start(
            string assemblyPath,
            string contractsDirectory,
            Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> callbackHandler,
            ILogger logger)
        {
            var launch = ResolveUnitHostLaunchCommand(assemblyPath, contractsDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = launch.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(assemblyPath) ?? Environment.CurrentDirectory,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var argument in launch.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ChildEnvironmentPolicy.Scrub(startInfo);
            startInfo.Environment["SPIKE_WINDOWS"] = "0";

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to launch sandbox worker '{assemblyPath}'.");

            WindowsJobObject? job = null;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    job = WindowsJobObject.Attach(process, logger);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killError)
                    {
                        logger.LogDebug(killError, "Cleanup kill after failed Windows Job assignment.");
                    }

                    process.Dispose();
                    throw;
                }
            }

            return new UnitHostChildSession(process, job, callbackHandler, logger);
        }

        public async Task<UnitHostFrameResponse> SendAsync(UnitHostFrameRequest request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (HasExited)
            {
                throw new InvalidOperationException($"Sandbox --unit-host process exited unexpectedly (exit code {_process.ExitCode}).");
            }

            var tcs = new TaskCompletionSource<UnitHostFrameResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[request.Id] = tcs;
            try
            {
                await WriteRequestLineAsync(request, cancellationToken).ConfigureAwait(false);
                using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(request.Id, out var _);
            }
        }

        private async Task WriteRequestLineAsync(UnitHostFrameRequest request, CancellationToken cancellationToken)
        {
            var line = JsonSerializer.Serialize(request, FrameJson);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadStdoutLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    UnitHostFrameResponse? frame;
                    try
                    {
                        frame = JsonSerializer.Deserialize<UnitHostFrameResponse>(line, FrameJson);
                    }
                    catch (JsonException error)
                    {
                        _logger.LogDebug(error, "Ignoring malformed frame from sandbox --unit-host.");
                        continue;
                    }

                    if (frame is null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(frame.CallbackKind))
                    {
                        _ = Task.Run(async () =>
                        {
                            UnitHostFrameRequest reply;
                            try
                            {
                                reply = await _callbackHandler(frame, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                _logger.LogWarning(error, "Sandbox callback '{CallbackKind}' failed for frame '{FrameId}'.", frame.CallbackKind, frame.Id);
                                reply = new UnitHostFrameRequest(
                                    frame.Id,
                                    "callback-reply",
                                    Ok: false,
                                    StatusCode: StatusCodes.Status500InternalServerError,
                                    InvokeResult: new SandboxEdgeEndpoints.EdgeInvokeResponse(null, error.Message),
                                    Error: error.Message);
                            }

                            try
                            {
                                await WriteRequestLineAsync(reply, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception writeError) when (writeError is not OperationCanceledException)
                            {
                                _logger.LogDebug(writeError, "Failed to write pipe callback reply '{FrameId}'.", frame.Id);
                            }
                        }, cancellationToken);
                        continue;
                    }

                    if (_pending.TryRemove(frame.Id, out var waiter))
                    {
                        waiter.TrySetResult(frame);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Sandbox --unit-host stdout reader stopped.");
            }
            finally
            {
                foreach (var waiter in _pending.Values)
                {
                    waiter.TrySetException(new InvalidOperationException("Sandbox --unit-host stream closed."));
                }

                _pending.Clear();
            }
        }

        private async Task ReadStderrLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger.LogDebug("[UnitHost stderr] {Line}", line);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Sandbox --unit-host stderr reader stopped.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (!HasExited)
            {
                try
                {
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    _ = await SendAsync(new UnitHostFrameRequest(NewFrameId(), "shutdown"), shutdownCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _logger.LogDebug(error, "Graceful --unit-host shutdown skipped.");
                }
            }

            await _cts.CancelAsync().ConfigureAwait(false);

            if (!HasExited)
            {
                try
                {
                    using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killError)
                    {
                        _logger.LogDebug(killError, "Kill during --unit-host shutdown.");
                    }
                }
            }

            try
            {
                await Task.WhenAll(_stdoutTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Waiting on --unit-host stream readers during dispose.");
            }

            _job?.Dispose();
            _process.Dispose();
            _writeLock.Dispose();
            _cts.Dispose();
        }

        private static string ResolveDotNetExecutable()
            => HttpSandboxRuntimeBridge.ResolveDotNetExecutable();
    }

    internal sealed record UnitHostLaunchCommand(string FileName, string[] Arguments);

    /// <summary>
    /// Resolves the command line for launching <c>IntoChat.OS.Sandbox --unit-host</c>.
    /// Prefers the bundled self-contained native apphost (<c>IntoChat.OS.Sandbox.exe</c> on Windows,
    /// <c>IntoChat.OS.Sandbox</c> on POSIX) when present so SDK-less desktop installs never invoke
    /// <c>dotnet</c>; falls back to <c>dotnet exec &lt;dll&gt; --unit-host</c> only when no native apphost
    /// exists alongside the <c>.dll</c>.
    /// </summary>
    internal static UnitHostLaunchCommand ResolveUnitHostLaunchCommand(
        string assemblyPath,
        string contractsDirectory,
        bool? isWindows = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var arguments = new List<string>(4);
        string fileName;

        if (TryResolveApphostExecutable(assemblyPath, windows, out var apphostPath))
        {
            fileName = apphostPath;
        }
        else
        {
            fileName = ResolveDotNetExecutable();
            arguments.Add("exec");
            arguments.Add(assemblyPath);
        }

        arguments.Add("--unit-host");
        if (!string.IsNullOrWhiteSpace(contractsDirectory))
        {
            arguments.Add("--contracts-dir");
            arguments.Add(contractsDirectory);
        }

        return new UnitHostLaunchCommand(fileName, [.. arguments]);
    }

    private static bool TryResolveApphostExecutable(string assemblyOrExecutablePath, bool isWindows, out string executablePath)
    {
        var extension = Path.GetExtension(assemblyOrExecutablePath);
        if (!string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(assemblyOrExecutablePath))
            {
                executablePath = Path.GetFullPath(assemblyOrExecutablePath);
                return true;
            }

            executablePath = string.Empty;
            return false;
        }

        var directory = Path.GetDirectoryName(assemblyOrExecutablePath);
        var baseName = Path.GetFileNameWithoutExtension(assemblyOrExecutablePath);
        if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(baseName))
        {
            var candidate = Path.Combine(directory, isWindows ? baseName + ".exe" : baseName);
            if (File.Exists(candidate))
            {
                executablePath = Path.GetFullPath(candidate);
                return true;
            }
        }

        executablePath = string.Empty;
        return false;
    }

    private static string ResolveDotNetExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    private sealed class WindowsJobObject : IDisposable
    {
        private readonly SafeJobHandle _handle;

        private WindowsJobObject(SafeJobHandle handle)
        {
            _handle = handle;
        }

        public static WindowsJobObject Attach(Process process, ILogger logger)
        {
            var rawJob = Native.CreateJobObjectW(IntPtr.Zero, null);
            var jobHandle = new SafeJobHandle(rawJob);
            if (jobHandle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                jobHandle.Dispose();
                throw new InvalidOperationException($"CreateJobObjectW failed with Win32 error {err}.");
            }

            try
            {
                var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                            | Native.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                    },
                };

                var size = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                    if (!Native.SetInformationJobObject(jobHandle, Native.JobObjectExtendedLimitInformation, ptr, size))
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new InvalidOperationException($"SetInformationJobObject failed with Win32 error {err}.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                if (!Native.AssignProcessToJobObject(jobHandle, process.Handle))
                {
                    var err = Marshal.GetLastWin32Error();
                    logger.LogDebug("AssignProcessToJobObject returned Win32 error {ErrorCode}; continuing under OS handle ownership.", err);
                }

                return new WindowsJobObject(jobHandle);
            }
            catch
            {
                jobHandle.Dispose();
                throw;
            }
        }

        public void Dispose() => _handle.Dispose();

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle()
                : base(ownsHandle: true)
            {
            }

            public SafeJobHandle(IntPtr handle)
                : base(ownsHandle: true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle() => Native.CloseHandle(handle);
        }

        private static class Native
        {
            public const int JobObjectExtendedLimitInformation = 9;
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
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            public static extern bool SetInformationJobObject(SafeJobHandle hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, int cbJobObjectInformationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            public static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }

    private sealed class DeployedAppEntry(
        string brainId,
        string appKey,
        AppPackage package,
        string[] signals,
        string[] schedules,
        string[] commands,
        string? stateJson,
        string? settingsJson,
        View? latestView,
        string[]? allowedContracts = null)
    {
        private long _viewGeneration;

        public string BrainId { get; } = brainId;
        public string AppKey { get; } = appKey;
        public AppPackage Package { get; } = package;
        public string[] Signals { get; } = signals;
        public string[] Schedules { get; } = schedules;
        public string[] Commands { get; } = commands;
        public string? StateJson { get; set; } = stateJson;
        public string? SettingsJson { get; set; } = settingsJson;
        public View? LatestView { get; set; } = latestView;
        public string[]? AllowedContracts { get; } = allowedContracts;

        public long NextViewGeneration() => Interlocked.Increment(ref _viewGeneration);
    }

    private sealed record WorkerCommandDescriptor(string Name, string Description);

    private sealed record DeployWorkerRequest(
        string BrainId,
        string AppKey,
        string Version,
        string Digest,
        string AssemblyBase64,
        string? StateJson,
        string? SettingsJson,
        string? KernelUrl,
        string? AppToken);

    private sealed record DeployWorkerResponse(
        string BrainId,
        string AppKey,
        string Version,
        string Digest,
        string Title,
        string Summary,
        WorkerCommandDescriptor[] Commands,
        string[] Signals,
        string[] Schedules,
        string? InitialViewJson,
        string? StateJson);

    private sealed record DispatchWorkerRequest(
        string BrainId,
        string AppKey,
        string Kind,
        string? Name,
        string? PayloadJson,
        string? StateJson,
        string? SettingsJson,
        string? KernelUrl,
        string? AppToken);

    private sealed record DispatchWorkerResponse(
        bool Handled,
        string? ResultJson,
        string? UpdatedStateJson,
        string? UpdatedViewJson,
        string? Error);

    private sealed record UnloadWorkerRequest(string BrainId, string AppKey);

    private sealed record UnloadWorkerResponse(bool Unloaded, string BrainId, string AppKey);

    private sealed record UnitHostFrameRequest(
        string Id,
        string Op,
        DeployWorkerRequest? Deploy = null,
        DispatchWorkerRequest? Dispatch = null,
        UnloadWorkerRequest? Unload = null,
        bool? Ok = null,
        int? StatusCode = null,
        SandboxEdgeEndpoints.EdgeInvokeResponse? InvokeResult = null,
        string? Error = null);

    private sealed record UnitHostFrameResponse(
        string Id,
        bool Ok,
        string? Error = null,
        DeployWorkerResponse? Deploy = null,
        DispatchWorkerResponse? Dispatch = null,
        UnloadWorkerResponse? Unload = null,
        IReadOnlyDictionary<string, string>? EnvironmentSnapshot = null,
        int? ProcessId = null,
        string? CallbackKind = null,
        string? BrainId = null,
        string? AppKey = null,
        string? AppToken = null,
        SandboxEdgeEndpoints.EdgeInvokeRequest? Invoke = null,
        SandboxEdgeEndpoints.EdgeSignalRequest? Signal = null,
        SandboxEdgeEndpoints.EdgeLogRequest? Log = null);
}
