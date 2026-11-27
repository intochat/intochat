using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.UI;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Apps;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Sandbox;

internal sealed partial class HttpSandboxRuntimeBridge
{
    internal static ConfinementSpec BuildUnitHostConfinementSpec(
        UnitHostLaunchCommand command,
        string assemblyPath,
        string contractsDirectory,
        string sessionWorkingDirectory,
        string? sandboxId = null)
        => UnitHostChildSession.BuildConfinementSpec(command, assemblyPath, contractsDirectory, sessionWorkingDirectory, sandboxId);

    private sealed class UnitHostChildSession : IAsyncDisposable
    {
        private readonly ConfinedProcessSession _session;
        private readonly string _workingDirectory;
        private readonly Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> _callbackHandler;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<UnitHostFrameResponse>> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _recentStderr = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private int _disposed;

        private UnitHostChildSession(
            ConfinedProcessSession session,
            string workingDirectory,
            Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> callbackHandler,
            ILogger logger)
        {
            _session = session;
            _workingDirectory = workingDirectory;
            _callbackHandler = callbackHandler;
            _logger = logger;
            _stdoutTask = Task.Run(() => ReadStdoutLoopAsync(_cts.Token));
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_cts.Token));
        }

        public ConfinementMode EffectiveMode => _session.EffectiveMode;

        public string TempDirectory => _session.TempDirectory;

        public string WorkingDirectory => _workingDirectory;

        public bool HasExited => _session.HasExited;

        internal static ConfinementSpec BuildConfinementSpec(
            UnitHostLaunchCommand command,
            string assemblyPath,
            string contractsDirectory,
            string sessionWorkingDirectory,
            string? sandboxId = null)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionWorkingDirectory);

            var readOnlyPaths = new List<string>();
            var assemblyDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (!string.IsNullOrWhiteSpace(assemblyDir) && Directory.Exists(assemblyDir))
            {
                readOnlyPaths.Add(assemblyDir);
            }

            if (!string.IsNullOrWhiteSpace(contractsDirectory))
            {
                var fullContractsDir = Path.GetFullPath(contractsDirectory);
                if (Directory.Exists(fullContractsDir)
                    && !readOnlyPaths.Contains(fullContractsDir, StringComparer.OrdinalIgnoreCase))
                {
                    readOnlyPaths.Add(fullContractsDir);
                }
            }

            var cleanEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPIKE_WINDOWS"] = "0",
            };

            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot) && Directory.Exists(dotnetRoot))
            {
                var fullDotnetRoot = Path.GetFullPath(dotnetRoot);
                cleanEnvironment["DOTNET_ROOT"] = fullDotnetRoot;
                if (!readOnlyPaths.Contains(fullDotnetRoot, StringComparer.OrdinalIgnoreCase))
                {
                    readOnlyPaths.Add(fullDotnetRoot);
                }
            }

            return new ConfinementSpec(
                SandboxId: string.IsNullOrWhiteSpace(sandboxId)
                    ? "unithost-" + Guid.NewGuid().ToString("N")[..12]
                    : sandboxId,
                ExecutablePath: command.FileName,
                Arguments: command.Arguments,
                WorkingDirectory: sessionWorkingDirectory,
                ReadOnlyPaths: readOnlyPaths,
                ReadWritePaths: [],
                CleanEnvironment: cleanEnvironment,
                Network: NetworkPolicy.None,
                MaxMemoryBytes: 1024L * 1024 * 1024,
                MaxProcesses: 16,
                PreferAppContainer: true,
                UseLessPrivilegedAppContainer: false);
        }

        public static async Task<UnitHostChildSession> StartAsync(
            string assemblyPath,
            string contractsDirectory,
            IConfinedProcessRunner confinedProcessRunner,
            Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> callbackHandler,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(confinedProcessRunner);
            ArgumentNullException.ThrowIfNull(callbackHandler);
            ArgumentNullException.ThrowIfNull(logger);

            var launchCommand = ResolveUnitHostLaunchCommand(assemblyPath, contractsDirectory);
            var session = StartCore(
                launchCommand,
                assemblyPath,
                contractsDirectory,
                confinedProcessRunner,
                callbackHandler,
                logger,
                preferAppContainer: true);

            try
            {
                await VerifyStartupHandshakeAsync(session, cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && session.EffectiveMode is ConfinementMode.AppContainer or ConfinementMode.LessPrivilegedAppContainer)
            {
                var failedMode = session.EffectiveMode;
                await session.DisposeAsync().ConfigureAwait(false);

                logger.LogWarning(
                    ex,
                    "Sandbox --unit-host failed startup handshake under {ConfinementMode} (e.g. CoreCLR user32/desktop initialization restriction); falling back to degraded JobObject confinement while unconditionally enforcing Win32 Job Object limits (1 GiB memory, 16 active processes, 0xFF UI restrictions, KILL_ON_JOB_CLOSE), 3-handle stdio allowlist, sanitized environment, and temp-dir purge.",
                    failedMode);

                var fallback = StartCore(
                    launchCommand,
                    assemblyPath,
                    contractsDirectory,
                    confinedProcessRunner,
                    callbackHandler,
                    logger,
                    preferAppContainer: false);
                try
                {
                    await VerifyStartupHandshakeAsync(fallback, cancellationToken).ConfigureAwait(false);
                    return fallback;
                }
                catch
                {
                    await fallback.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static UnitHostChildSession StartCore(
            UnitHostLaunchCommand launchCommand,
            string assemblyPath,
            string contractsDirectory,
            IConfinedProcessRunner confinedProcessRunner,
            Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> callbackHandler,
            ILogger logger,
            bool preferAppContainer)
        {
            var sandboxId = "unithost-" + Guid.NewGuid().ToString("N")[..12];
            var sessionWorkingDirectory = Path.Combine(Path.GetTempPath(), "intochat-unithost", sandboxId);
            Directory.CreateDirectory(sessionWorkingDirectory);

            try
            {
                var spec = BuildConfinementSpec(
                    launchCommand,
                    assemblyPath,
                    contractsDirectory,
                    sessionWorkingDirectory,
                    sandboxId);
                if (!preferAppContainer)
                {
                    spec = spec with { PreferAppContainer = false, UseLessPrivilegedAppContainer = false };
                }

                var session = confinedProcessRunner.StartSession(spec);
                logger.LogInformation(
                    "Started confined sandbox --unit-host (PID={ProcessId}, Mode={ConfinementMode}, SandboxId={SandboxId}).",
                    session.ProcessId,
                    session.EffectiveMode,
                    sandboxId);

                return new UnitHostChildSession(session, sessionWorkingDirectory, callbackHandler, logger);
            }
            catch
            {
                TryDeleteWorkingDirectory(sessionWorkingDirectory, logger);
                throw;
            }
        }

        private static async Task VerifyStartupHandshakeAsync(UnitHostChildSession session, CancellationToken cancellationToken)
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
            var ping = await session.SendAsync(new UnitHostFrameRequest(NewFrameId(), "ping"), handshakeCts.Token)
                .ConfigureAwait(false);
            if (!ping.Ok)
            {
                throw new InvalidOperationException(
                    $"Sandbox --unit-host startup ping failed (mode={session.EffectiveMode}): {ping.Error ?? "Unknown error."}");
            }
        }

        public async Task<UnitHostFrameResponse> SendAsync(UnitHostFrameRequest request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (HasExited)
            {
                throw new InvalidOperationException($"Sandbox --unit-host process exited unexpectedly (exit code {_session.ExitCode}).");
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
                await _session.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                await _session.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                    var line = await _session.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
                var stderrTail = string.Join(" | ", _recentStderr);
                var message = string.IsNullOrWhiteSpace(stderrTail)
                    ? $"Sandbox --unit-host stream closed (mode={_session.EffectiveMode}, exitCode={_session.ExitCode})."
                    : $"Sandbox --unit-host stream closed (mode={_session.EffectiveMode}, exitCode={_session.ExitCode}): {stderrTail}";
                foreach (var waiter in _pending.Values)
                {
                    waiter.TrySetException(new InvalidOperationException(message));
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
                    var line = await _session.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _recentStderr.Enqueue(line);
                        while (_recentStderr.Count > 8)
                        {
                            _recentStderr.TryDequeue(out _);
                        }

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
                    await _session.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
                {
                    try
                    {
                        _session.Kill();
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

            await _session.DisposeAsync().ConfigureAwait(false);
            TryDeleteWorkingDirectory(_workingDirectory, _logger);
            _writeLock.Dispose();
            _cts.Dispose();
        }

        private static void TryDeleteWorkingDirectory(string directory, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        return;
                    }

                    Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 4)
                    {
                        logger.LogWarning(ex, "Could not recursively delete --unit-host working directory {WorkingDirectory}.", directory);
                        return;
                    }

                    Thread.Sleep(25 * (attempt + 1));
                }
            }
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
