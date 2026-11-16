using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Execution;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Runtime bridge between <see cref="AppNeuron"/> and the isolated <c>IntoChat.OS.Sandbox</c> worker.
/// Routes to the remote Docker worker when <see cref="SandboxOptions.Url"/> is configured, and
/// executes out-of-process in an environment-scrubbed <c>IntoChat.OS.Sandbox --unit-host</c> child
/// process over NDJSON stdio pipes otherwise.
/// </summary>
internal sealed class HttpSandboxRuntimeBridge(
    IHttpClientFactory httpClientFactory,
    IOptions<SandboxOptions> options,
    AppTokenService tokens,
    IServiceProvider services,
    ILogger<HttpSandboxRuntimeBridge> logger) : ISandboxRuntimeBridge, IAsyncDisposable
{
    private const int MaxWorkerAttempts = 20;

    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, DeployedAppEntry> _deployments = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _unitHostLock = new(1, 1);
    private UnitHostChildSession? _unitHost;
    private int _disposed;

    public async Task<SandboxDeploymentResult> DeployPackageAsync(
        string brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var key = DeploymentKey(brainId, package.Key);
        var token = tokens.Issue(brainId, package.Key);
        var kernelUrl = ResolveKernelCallbackUrl();
        var request = new DeployWorkerRequest(
            brainId,
            package.Key,
            package.Version,
            package.Digest,
            Convert.ToBase64String(assemblyBytes),
            stateJson,
            settingsJson,
            kernelUrl,
            token);

        DeployWorkerResponse response;
        var workerUrl = options.Value.Url;
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            var frame = await SendToUnitHostAsync(
                new UnitHostFrameRequest(
                    Id: NewFrameId(),
                    Op: "deploy",
                    Deploy: request),
                cancellationToken).ConfigureAwait(false);
            if (!frame.Ok || frame.Deploy is null)
            {
                throw new InvalidOperationException(
                    $"Sandbox --unit-host deploy failed for '{brainId}/{package.Key}': {frame.Error ?? "Empty deploy response."}");
            }

            response = frame.Deploy;
        }
        else
        {
            response = await PostToWorkerAsync<DeployWorkerRequest, DeployWorkerResponse>(
                workerUrl,
                "/apps/deploy",
                request,
                cancellationToken).ConfigureAwait(false);
        }

        var parsedView = DeserializeView(response.InitialViewJson);
        var entry = new DeployedAppEntry(
            brainId,
            package.Key,
            package,
            response.Signals,
            response.Schedules,
            [.. response.Commands.Select(c => c.Name)],
            response.StateJson ?? stateJson,
            settingsJson,
            parsedView);
        _deployments[key] = entry;

        await PublishViewAsync(entry, parsedView, cancellationToken).ConfigureAwait(false);

        return new SandboxDeploymentResult(
            Succeeded: true,
            Error: null,
            response.Signals,
            response.Schedules,
            [.. response.Commands.Select(c => c.Name)],
            parsedView);
    }

    public Task<SandboxDispatchResult> DispatchSignalAsync(
        string brainId,
        string appKey,
        string signalName,
        string payloadJson,
        CancellationToken cancellationToken = default)
        => DispatchAsync(brainId, appKey, "signal", signalName, payloadJson, cancellationToken);

    public Task<SandboxDispatchResult> DispatchScheduleAsync(
        string brainId,
        string appKey,
        string cron,
        CancellationToken cancellationToken = default)
        => DispatchAsync(brainId, appKey, "schedule", cron, null, cancellationToken);

    public Task<SandboxDispatchResult> DispatchCommandAsync(
        string brainId,
        string appKey,
        string command,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
        => DispatchAsync(brainId, appKey, "command", command, payloadJson, cancellationToken);

    public async Task<View?> RenderAsync(
        string brainId,
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var key = DeploymentKey(brainId, appKey);
        if (!_deployments.TryGetValue(key, out var entry))
        {
            return null;
        }

        var result = await DispatchAsync(brainId, appKey, "render", null, null, cancellationToken).ConfigureAwait(false);
        return result.UpdatedView ?? entry.LatestView;
    }

    public async Task ForwardSignalAsync(
        string brainId,
        string signalName,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);

        foreach (var entry in _deployments.Values)
        {
            if (!string.Equals(entry.BrainId, brainId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.Signals.Any(sig => string.Equals(sig, signalName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var result = await DispatchSignalAsync(brainId, entry.AppKey, signalName, payloadJson, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                logger.LogWarning(
                    "Signal '{Signal}' dispatch to sandbox app '{BrainId}/{AppKey}' reported error: {Error}",
                    signalName,
                    brainId,
                    entry.AppKey,
                    result.Error);
            }
        }
    }

    public async Task UnloadAsync(
        string brainId,
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var key = DeploymentKey(brainId, appKey);
        _deployments.TryRemove(key, out _);

        var workerUrl = options.Value.Url;
        if (string.IsNullOrWhiteSpace(workerUrl))
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
                    logger.LogSwallowed(error, $"Sandbox --unit-host unload failed for '{brainId}/{appKey}'.");
                }
            }

            return;
        }

        try
        {
            _ = await PostToWorkerAsync<UnloadWorkerRequest, UnloadWorkerResponse>(
                workerUrl,
                "/apps/unload",
                new UnloadWorkerRequest(brainId, appKey),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, $"Sandbox unload failed for '{brainId}/{appKey}'.");
        }
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

    private async Task<SandboxDispatchResult> DispatchAsync(
        string brainId,
        string appKey,
        string kind,
        string? name,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        cancellationToken.ThrowIfCancellationRequested();

        var key = DeploymentKey(brainId, appKey);
        _deployments.TryGetValue(key, out var existing);

        var token = tokens.Issue(brainId, appKey);
        var kernelUrl = ResolveKernelCallbackUrl();
        var request = new DispatchWorkerRequest(
            brainId,
            appKey,
            kind,
            name,
            payloadJson,
            existing?.StateJson,
            existing?.SettingsJson,
            kernelUrl,
            token);

        DispatchWorkerResponse response;
        var workerUrl = options.Value.Url;
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            var frame = await SendToUnitHostAsync(
                new UnitHostFrameRequest(
                    Id: NewFrameId(),
                    Op: "dispatch",
                    Dispatch: request),
                cancellationToken).ConfigureAwait(false);
            if (!frame.Ok || frame.Dispatch is null)
            {
                return new SandboxDispatchResult(
                    Handled: false,
                    ResultJson: null,
                    UpdatedStateJson: existing?.StateJson,
                    UpdatedView: existing?.LatestView,
                    Error: frame.Error ?? "Sandbox --unit-host dispatch failed.");
            }

            response = frame.Dispatch;
        }
        else
        {
            response = await PostToWorkerAsync<DispatchWorkerRequest, DispatchWorkerResponse>(
                workerUrl,
                "/apps/dispatch",
                request,
                cancellationToken).ConfigureAwait(false);
        }

        var updatedView = DeserializeView(response.UpdatedViewJson);
        if (existing is not null)
        {
            existing.StateJson = response.UpdatedStateJson ?? existing.StateJson;
            if (updatedView is not null)
            {
                existing.LatestView = updatedView;
            }

            await PublishViewAsync(existing, updatedView, cancellationToken).ConfigureAwait(false);
        }

        return new SandboxDispatchResult(
            response.Handled,
            response.ResultJson,
            response.UpdatedStateJson,
            updatedView,
            response.Error);
    }

    private async Task<UnitHostFrameResponse> SendToUnitHostAsync(UnitHostFrameRequest frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = await EnsureUnitHostSessionAsync(cancellationToken).ConfigureAwait(false);
        return await session.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UnitHostChildSession> EnsureUnitHostSessionAsync(CancellationToken cancellationToken)
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

            var assemblyPath = ResolveUnitHostAssemblyPath(options.Value.UnitHostAssemblyPath);
            var contractsDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var session = UnitHostChildSession.Start(
                assemblyPath,
                contractsDir,
                HandlePipeCallbackAsync,
                logger);
            _unitHost = session;
            return session;
        }
        finally
        {
            _unitHostLock.Release();
        }
    }

    private async Task<UnitHostFrameRequest> HandlePipeCallbackAsync(UnitHostFrameResponse frame, CancellationToken cancellationToken)
    {
        var kind = frame.CallbackKind ?? string.Empty;
        if (string.Equals(kind, "log", StringComparison.OrdinalIgnoreCase))
        {
            if (SandboxEdgeEndpoints.TryAuthenticateToken(frame.AppToken, frame.BrainId, frame.AppKey, tokens, out var logIdentity)
                && frame.Log is { } logEntry)
            {
                logger.LogInformation(
                    "[{BrainId}/{AppKey}] ({TimestampUtc}) {Message}",
                    logIdentity.BrainId,
                    logIdentity.AppKey,
                    logEntry.TimestampUtc,
                    logEntry.Message);
                return new UnitHostFrameRequest(frame.Id, "callback-reply", Ok: true, StatusCode: StatusCodes.Status200OK);
            }

            return new UnitHostFrameRequest(frame.Id, "callback-reply", Ok: false, StatusCode: StatusCodes.Status401Unauthorized, Error: "Unauthorized.");
        }

        if (string.Equals(kind, "signal", StringComparison.OrdinalIgnoreCase))
        {
            if (!SandboxEdgeEndpoints.TryAuthenticateToken(frame.AppToken, frame.BrainId, frame.AppKey, tokens, out var signalIdentity)
                || frame.Signal is null)
            {
                return new UnitHostFrameRequest(frame.Id, "callback-reply", Ok: false, StatusCode: StatusCodes.Status401Unauthorized, Error: "Unauthorized.");
            }

            await ForwardSignalAsync(signalIdentity.BrainId, frame.Signal.SignalName, frame.Signal.PayloadJson, cancellationToken)
                .ConfigureAwait(false);
            return new UnitHostFrameRequest(frame.Id, "callback-reply", Ok: true, StatusCode: StatusCodes.Status200OK);
        }

        if (string.Equals(kind, "invoke", StringComparison.OrdinalIgnoreCase))
        {
            if (!SandboxEdgeEndpoints.TryAuthenticateToken(frame.AppToken, frame.BrainId, frame.AppKey, tokens, out var identity))
            {
                return new UnitHostFrameRequest(
                    frame.Id,
                    "callback-reply",
                    Ok: false,
                    StatusCode: StatusCodes.Status401Unauthorized,
                    InvokeResult: new SandboxEdgeEndpoints.EdgeInvokeResponse(null, "Unauthorized."),
                    Error: "Unauthorized.");
            }

            if (frame.Invoke is null)
            {
                return new UnitHostFrameRequest(
                    frame.Id,
                    "callback-reply",
                    Ok: false,
                    StatusCode: StatusCodes.Status422UnprocessableEntity,
                    InvokeResult: new SandboxEdgeEndpoints.EdgeInvokeResponse(null, "Invoke payload is required."),
                    Error: "Invoke payload is required.");
            }

            var ai = services.GetRequiredService<SdkAiAdapter>();
            var tables = services.GetRequiredService<SdkTablesAdapter>();
            var outcome = await SandboxEdgeEndpoints.InvokeEdgeAsync(identity, frame.Invoke, ai, tables, services, cancellationToken)
                .ConfigureAwait(false);
            return new UnitHostFrameRequest(
                frame.Id,
                "callback-reply",
                Ok: outcome.StatusCode == StatusCodes.Status200OK,
                StatusCode: outcome.StatusCode,
                InvokeResult: outcome.Response,
                Error: outcome.Response.Error);
        }

        return new UnitHostFrameRequest(frame.Id, "callback-reply", Ok: false, StatusCode: StatusCodes.Status404NotFound, Error: $"Unsupported callback '{kind}'.");
    }

    private string ResolveKernelCallbackUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.KernelCallbackUrl))
        {
            return options.Value.KernelCallbackUrl!;
        }

        var server = services.GetService<IServer>();
        var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null)
        {
            foreach (var raw in addresses)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var normalized = raw
                    .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://[::]:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal);

                if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    && uri.Port > 0)
                {
                    return uri.ToString().TrimEnd('/');
                }
            }
        }

        return "pipe://edge";
    }

    private static string ResolveUnitHostAssemblyPath(string? configuredPath)
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
        var baseCandidate = Path.Combine(baseDir, "IntoChat.OS.Sandbox.dll");
        if (HasValidRuntimeConfig(baseCandidate))
        {
            return baseCandidate;
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

    private static bool HasValidRuntimeConfig(string dllPath)
        => File.Exists(dllPath) && File.Exists(Path.ChangeExtension(dllPath, ".runtimeconfig.json"));

    private async Task<TResponse> PostToWorkerAsync<TRequest, TResponse>(
        string workerBaseUrl,
        string path,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(workerBaseUrl.TrimEnd('/') + path, UriKind.Absolute);
        var client = httpClientFactory.CreateClient("SandboxWorker");

        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload, options: JsonSerializerOptions.Web),
            };

            if (!string.IsNullOrWhiteSpace(options.Value.EnrollKey))
            {
                message.Headers.TryAddWithoutValidation("X-Sandbox-Enroll-Key", options.Value.EnrollKey);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (attempt < MaxWorkerAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    error,
                    "Sandbox worker '{Path}' not reachable yet (attempt {Attempt}/{MaxAttempts}); retrying.",
                    path,
                    attempt,
                    MaxWorkerAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Sandbox worker '{path}' returned {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}");
                }

                var parsed = await response.Content.ReadFromJsonAsync<TResponse>(JsonSerializerOptions.Web, cancellationToken)
                    .ConfigureAwait(false);
                return parsed ?? throw new InvalidOperationException($"Sandbox worker '{path}' returned an empty body.");
            }
        }
    }

    private async Task PublishViewAsync(DeployedAppEntry entry, View? view, CancellationToken cancellationToken)
    {
        if (view is null || services.GetService<Orleans.IGrainFactory>() is null)
        {
            return;
        }

        try
        {
            var generation = entry.NextViewGeneration();
            var brainId = new BrainId(entry.BrainId);
            var appRef = IntoChat.OS.Primitives.AppRef.TryParse(brainId, entry.AppKey, out var parsed)
                ? parsed
                : IntoChat.OS.Primitives.AppRef.Of(brainId, entry.AppKey);
            var identityClient = services.GetService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>();
            var owner = identityClient is not null
                ? await identityClient.OwnerOfBrain(brainId.Value).ConfigureAwait(false)
                : null;
            var resolvedAccount = string.IsNullOrWhiteSpace(owner?.AccountId) ? brainId.Value : owner.AccountId;
            var appPrincipal = DigitalBrain.Capabilities.AppPrincipal.Id(appRef.Key);
            var edge = services.GetService<DigitalBrain.Access.EdgeAuthority>();
            using (edge?.EnterInProcess())
            using (edge?.Enter(edge.App(appRef.Key, new DigitalBrain.Access.Actor.Person(appPrincipal, resolvedAccount), brainId)))
            {
                var viewId = new NeuronId(brainId, IntoChat.OS.Modules.Flutter.UIVocabulary.ViewType, appRef.ViewPath);
                await SiloNeurons.Get<IView>(services, viewId).Publish(generation, view).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, $"Optional UI view publication skipped for '{entry.BrainId}/{entry.AppKey}'.");
        }
    }

    private View? DeserializeView(string? viewJson)
    {
        if (string.IsNullOrWhiteSpace(viewJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<View>(viewJson, RuntimeProtocol.OptionsFor(typeof(View)));
        }
        catch (JsonException error)
        {
            logger.LogSwallowed(error, "Could not parse sandbox view JSON; retrying with web defaults.");
            return JsonSerializer.Deserialize<View>(viewJson, JsonSerializerOptions.Web);
        }
    }

    private static string NewFrameId() => Guid.NewGuid().ToString("N");

    private static string DeploymentKey(string brainId, string appKey) => $"{brainId}/{appKey}";

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
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotNetExecutable(),
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

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(assemblyPath);
            startInfo.ArgumentList.Add("--unit-host");
            if (!string.IsNullOrWhiteSpace(contractsDirectory))
            {
                startInfo.ArgumentList.Add("--contracts-dir");
                startInfo.ArgumentList.Add(contractsDirectory);
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
        View? latestView)
    {
        private long _viewGeneration;

        public string BrainId { get; } = brainId;
        public string AppKey { get; } = appKey;
        public AppPackage Package { get; } = package;
        public string[] Signals { get; } = signals;
        public string[] Schedules { get; } = schedules;
        public string[] Commands { get; } = commands;
        public string? StateJson { get; set; } = stateJson;
        public string? SettingsJson { get; } = settingsJson;
        public View? LatestView { get; set; } = latestView;

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
