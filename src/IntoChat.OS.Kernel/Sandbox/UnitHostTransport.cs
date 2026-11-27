using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Devices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Manages the confined out-of-process <c>IntoChat.OS.Sandbox --unit-host</c> child session,
/// including binary/apphost discovery, confinement specification, AppContainer-to-JobObject fallback,
/// NDJSON stdio framing, and local connected-sandbox execution.
/// </summary>
internal sealed class UnitHostTransport(
    IOptions<SandboxOptions> options,
    IServiceProvider services,
    SandboxCallbackProxy callbackProxy,
    ILogger logger,
    IConfinedProcessRunner? confinedProcessRunner = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _unitHostLock = new(1, 1);
    private UnitHostChildSession? _unitHost;
    private int _disposed;

    internal ConfinementMode? ActiveConfinementMode => _unitHost?.EffectiveMode;

    internal string? ActiveTempDirectory => _unitHost?.TempDirectory;

    internal string? ActiveWorkingDirectory => _unitHost?.WorkingDirectory;

    internal async Task<IReadOnlyDictionary<string, string>> ProbeEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new UnitHostFrameRequest(Id: NewFrameId(), Op: "env"),
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.EnvironmentSnapshot is null)
        {
            throw new InvalidOperationException(
                $"Sandbox --unit-host env probe failed: {response.Error ?? "Empty environment snapshot."}");
        }

        return response.EnvironmentSnapshot;
    }

    internal async Task<UnitHostFrameResponse> SendAsync(UnitHostFrameRequest frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        return await session.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    internal async Task TryUnloadAsync(string brainId, string appKey, string contextDescription, CancellationToken cancellationToken)
    {
        if (_unitHost is { } activeHost && !activeHost.HasExited)
        {
            try
            {
                _ = await activeHost.SendAsync(
                    new UnitHostFrameRequest(
                        Id: NewFrameId(),
                        Op: "unload",
                        Unload: new UnloadWorkerRequest(brainId, appKey)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogSwallowed(error, contextDescription);
            }
        }
    }

    internal async Task<ConnectedSandboxResponse> ExecuteLocalAsync(
        ConnectedSandboxRequest request,
        Func<string, string, string> deploymentKeyFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BrainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppToken);
        ct.ThrowIfCancellationRequested();

        var machineId = services.GetService<MachineId>()?.Value;
        var deploymentKey = deploymentKeyFactory(request.BrainId, request.AppKey);
        var remoteCallbackRegistrationId = callbackProxy.RegisterForRemoteJob(
            deploymentKey,
            request.JobId,
            request.BrainId,
            request.AppKey,
            request.KernelCallbackUrl);

        // Always route callbacks from the confined --unit-host child over its stdio pipe so the
        // child process runs with NetworkPolicy.None (zero sockets); parent Kernel forwards HTTP when remote.
        const string unitHostCallbackUrl = "pipe://edge";
        try
        {
            var deployFrame = await SendAsync(
                new UnitHostFrameRequest(
                    Id: NewFrameId(),
                    Op: "deploy",
                    Deploy: new DeployWorkerRequest(
                        request.BrainId,
                        request.AppKey,
                        request.Version,
                        request.Digest,
                        request.AssemblyBase64,
                        request.StateJson,
                        request.SettingsJson,
                        unitHostCallbackUrl,
                        request.AppToken)),
                ct).ConfigureAwait(false);

            if (!deployFrame.Ok || deployFrame.Deploy is null)
            {
                return new ConnectedSandboxResponse(
                    Handled: false,
                    ResultJson: null,
                    UpdatedStateJson: null,
                    Error: deployFrame.Error ?? "Local --unit-host deploy failed.",
                    ExecutedByMachineId: machineId);
            }

            try
            {
                var dispatchFrame = await SendAsync(
                    new UnitHostFrameRequest(
                        Id: NewFrameId(),
                        Op: "dispatch",
                        Dispatch: new DispatchWorkerRequest(
                            request.BrainId,
                            request.AppKey,
                            Kind: "command",
                            Name: request.Command,
                            PayloadJson: request.PayloadJson,
                            StateJson: deployFrame.Deploy.StateJson ?? request.StateJson,
                            SettingsJson: request.SettingsJson,
                            KernelUrl: unitHostCallbackUrl,
                            AppToken: request.AppToken)),
                    ct).ConfigureAwait(false);

                if (!dispatchFrame.Ok || dispatchFrame.Dispatch is null)
                {
                    return new ConnectedSandboxResponse(
                        Handled: false,
                        ResultJson: null,
                        UpdatedStateJson: null,
                        Error: dispatchFrame.Error ?? "Local --unit-host dispatch failed.",
                        ExecutedByMachineId: machineId);
                }

                return new ConnectedSandboxResponse(
                    Handled: dispatchFrame.Dispatch.Handled,
                    ResultJson: dispatchFrame.Dispatch.ResultJson,
                    UpdatedStateJson: dispatchFrame.Dispatch.UpdatedStateJson,
                    Error: dispatchFrame.Dispatch.Error,
                    ExecutedByMachineId: machineId);
            }
            finally
            {
                await TryUnloadAsync(
                    request.BrainId,
                    request.AppKey,
                    $"Local --unit-host unload failed for connected sandbox job '{request.JobId}'.",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (remoteCallbackRegistrationId is { } registrationId)
            {
                callbackProxy.UnregisterRemoteCallback(deploymentKey, registrationId);
            }
        }
    }

    internal IConfinedProcessRunner ResolveConfinedProcessRunner()
    {
        if (confinedProcessRunner is not null)
        {
            return confinedProcessRunner;
        }

        if (services.GetService<IConfinedProcessRunner>() is { } registered)
        {
            return registered;
        }

        var loggerFactory = services.GetService<ILoggerFactory>();
        return OperatingSystem.IsWindows()
            ? new WindowsAppContainerRunner(loggerFactory?.CreateLogger<WindowsAppContainerRunner>())
            : new PosixConfinedProcessRunner(loggerFactory?.CreateLogger<PosixConfinedProcessRunner>());
    }

    internal static ConfinementSpec BuildConfinementSpec(
        UnitHostLaunchCommand command,
        string assemblyPath,
        string contractsDirectory,
        string sessionWorkingDirectory,
        string? sandboxId = null)
        => UnitHostChildSession.BuildConfinementSpec(command, assemblyPath, contractsDirectory, sessionWorkingDirectory, sandboxId);

    /// <summary>
    /// Resolves the command line for launching <c>IntoChat.OS.Sandbox --unit-host</c>.
    /// Prefers the bundled self-contained native apphost (<c>IntoChat.OS.Sandbox.exe</c> on Windows,
    /// <c>IntoChat.OS.Sandbox</c> on POSIX) when present so SDK-less desktop installs never invoke
    /// <c>dotnet</c>; falls back to <c>dotnet exec &lt;dll&gt; --unit-host</c> only when no native apphost
    /// exists alongside the <c>.dll</c>.
    /// </summary>
    internal static UnitHostLaunchCommand ResolveLaunchCommand(
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

    internal static string ResolveAssemblyPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            throw new FileNotFoundException($"Configured UnitHostAssemblyPath '{configuredPath}' was not found.", configuredPath);
        }

        var baseDir = AppContext.BaseDirectory;
        var nativeBinaryName = OperatingSystem.IsWindows() ? "IntoChat.OS.Sandbox.exe" : "IntoChat.OS.Sandbox";
        string[] nativeCandidates =
        [
            Path.Combine(baseDir, nativeBinaryName),
            Path.Combine(baseDir, "unithost", nativeBinaryName),
            Path.Combine(baseDir, "..", "unithost", nativeBinaryName),
        ];
        foreach (var native in nativeCandidates)
        {
            if (File.Exists(native))
            {
                return Path.GetFullPath(native);
            }
        }

        string[] relativeCandidates =
        [
            Path.Combine(baseDir, "IntoChat.OS.Sandbox.dll"),
            Path.Combine(baseDir, "unithost", "IntoChat.OS.Sandbox.dll"),
            Path.Combine(baseDir, "..", "unithost", "IntoChat.OS.Sandbox.dll"),
        ];
        foreach (var rel in relativeCandidates)
        {
            if (HasValidRuntimeConfig(rel))
            {
                return Path.GetFullPath(rel);
            }
        }

        var current = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            string[] candidates =
            [
                Path.Combine(current.FullName, "artifacts", "bin", "IntoChat.OS.Sandbox", "release", "IntoChat.OS.Sandbox.dll"),
                Path.Combine(current.FullName, "artifacts", "bin", "IntoChat.OS.Sandbox", "debug", "IntoChat.OS.Sandbox.dll"),
                Path.Combine(current.FullName, "src", "IntoChat.OS.Sandbox", "bin", "Release", "net11.0", "IntoChat.OS.Sandbox.dll"),
                Path.Combine(current.FullName, "src", "IntoChat.OS.Sandbox", "bin", "Debug", "net11.0", "IntoChat.OS.Sandbox.dll"),
            ];

            foreach (var candidate in candidates)
            {
                if (HasValidRuntimeConfig(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"Could not locate IntoChat.OS.Sandbox.dll with a valid .runtimeconfig.json starting from '{baseDir}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _unitHostLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_unitHost is { } session)
            {
                _unitHost = null;
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _unitHostLock.Release();
            _unitHostLock.Dispose();
        }
    }

    private async Task<UnitHostChildSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        var current = _unitHost;
        if (current is not null && !current.HasExited)
        {
            return current;
        }

        await _unitHostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_unitHost is not null && !_unitHost.HasExited)
            {
                return _unitHost;
            }

            if (_unitHost is { } stale)
            {
                _unitHost = null;
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            var assemblyPath = ResolveAssemblyPath(options.Value.UnitHostAssemblyPath);
            var contractsDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var runner = ResolveConfinedProcessRunner();
            var session = await UnitHostChildSession.StartAsync(
                assemblyPath,
                contractsDir,
                runner,
                callbackProxy.HandlePipeCallbackAsync,
                logger,
                cancellationToken).ConfigureAwait(false);
            _unitHost = session;
            return session;
        }
        finally
        {
            _unitHostLock.Release();
        }
    }

    private static bool HasValidRuntimeConfig(string dllPath)
        => File.Exists(dllPath) && File.Exists(Path.ChangeExtension(dllPath, ".runtimeconfig.json"));

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

    internal static string NewFrameId() => Guid.NewGuid().ToString("N");

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

            var launchCommand = ResolveLaunchCommand(assemblyPath, contractsDirectory);
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
            var line = JsonSerializer.Serialize(request, UnitHostFrameJsonContext.Default.UnitHostFrameRequest);
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
                        frame = JsonSerializer.Deserialize(line, UnitHostFrameJsonContext.Default.UnitHostFrameResponse);
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
    }
}

internal sealed record UnitHostLaunchCommand(string FileName, string[] Arguments);

internal sealed record UnitHostFrameRequest(
    string Id,
    string Op,
    DeployWorkerRequest? Deploy = null,
    DispatchWorkerRequest? Dispatch = null,
    UnloadWorkerRequest? Unload = null,
    bool? Ok = null,
    int? StatusCode = null,
    SandboxEdgeEndpoints.EdgeInvokeResponse? InvokeResult = null,
    string? Error = null);

internal sealed record UnitHostFrameResponse(
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
