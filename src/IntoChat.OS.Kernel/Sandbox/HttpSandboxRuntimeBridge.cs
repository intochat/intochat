using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using IntoChat.OS.Host.Execution;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Runtime bridge between <see cref="AppNeuron"/> / <c>CodeOrchestrationSynapse</c> and the isolated
/// <c>IntoChat.OS.Sandbox</c> worker. For scoped single-shot code orchestration, routes deterministically:
/// 1. Owner's online consented <see cref="ICodeSandbox"/> connected client via <see cref="IConnectedComputeRouter"/>
///    (&lt;500 ms unavailability detection),
/// 2. Dedicated cloud sandbox Container App (<see cref="SandboxOptions.Url"/>) or environment-scrubbed local
///    <c>IntoChat.OS.Sandbox --unit-host</c> child process when <see cref="SandboxOptions.EnableLocalSandbox"/> is enabled,
/// 3. Headless Azure Container Apps Dynamic Sessions (Kata MicroVMs) via <see cref="AcaDynamicSessionsSandbox"/>
///    when configured and neither sovereign client device nor local sandbox is available/enabled.
/// </summary>
internal sealed partial class HttpSandboxRuntimeBridge(
    IHttpClientFactory httpClientFactory,
    IOptions<SandboxOptions> options,
    AppTokenService tokens,
    IServiceProvider services,
    ILogger<HttpSandboxRuntimeBridge> logger,
    IConnectedComputeRouter? connectedComputeRouter = null,
    AcaDynamicSessionsSandbox? acaDynamicSessionsFallback = null) : ISandboxRuntimeBridge, ILocalSandboxExecutor, IAsyncDisposable
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

    public Task<SandboxDeploymentResult> DeployPackageAsync(
        string brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        CancellationToken cancellationToken = default)
        => DeployPackageCoreAsync(ParseBrain(brainId), package, assemblyBytes, stateJson, settingsJson, allowedContracts: null, cancellationToken);

    public Task<SandboxDeploymentResult> DeployScopedPackageAsync(
        BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedContracts);
        return DeployPackageCoreAsync(brainId, package, assemblyBytes, stateJson, settingsJson, allowedContracts, cancellationToken);
    }

    public async Task<SandboxDispatchResult> ExecuteScopedCommandAsync(
        BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string command,
        string? payloadJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(allowedContracts);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedScope = IntoChat.OS.Host.Surface.NeuronContractDiscovery.NormalizeScopeList(allowedContracts);

        // Tier 1: Sovereign paired client device advertising ICodeSandbox.
        var router = connectedComputeRouter ?? services.GetService<IConnectedComputeRouter>();
        if (router is not null)
        {
            var scopedToken = tokens.IssueScoped(brainId, package.Key, normalizedScope);
            var kernelCallbackUrl = ResolveKernelCallbackUrl();
            var remoteRequest = new ConnectedSandboxRequest(
                JobId: NewFrameId(),
                BrainId: brainId.Value,
                AppKey: package.Key,
                Version: package.Version,
                Digest: package.Digest,
                AssemblyBase64: Convert.ToBase64String(assemblyBytes),
                Command: command,
                PayloadJson: payloadJson,
                StateJson: null,
                SettingsJson: null,
                KernelCallbackUrl: kernelCallbackUrl,
                AppToken: scopedToken,
                AllowedContracts: normalizedScope);

            var remoteResponse = await router
                .TryExecuteOnConnectedClientAsync(brainId, remoteRequest, cancellationToken)
                .ConfigureAwait(false);
            if (remoteResponse is not null)
            {
                logger.LogInformation(
                    "Executed scoped sandbox command '{Command}' for '{BrainId}/{AppKey}' on connected client '{MachineId}'.",
                    command,
                    brainId.Value,
                    package.Key,
                    remoteResponse.ExecutedByMachineId ?? "unknown");
                return new SandboxDispatchResult(
                    remoteResponse.Handled,
                    remoteResponse.ResultJson,
                    remoteResponse.UpdatedStateJson,
                    UpdatedView: null,
                    remoteResponse.Error,
                    ExecutionTarget: "connected-client",
                    ExecutedByMachineId: remoteResponse.ExecutedByMachineId);
            }

            logger.LogDebug(
                "No online consented ICodeSandbox connected client answered within availability budget for '{BrainId}/{AppKey}'; proceeding to fallback routing.",
                brainId.Value,
                package.Key);
        }

        var acaFallback = acaDynamicSessionsFallback ?? services.GetService<AcaDynamicSessionsSandbox>();
        var hasDedicatedWorkerUrl = !string.IsNullOrWhiteSpace(options.Value.Url);

        // Tier 2: Dedicated sandbox worker URL or local desktop sandbox (--unit-host) when enabled.
        if (hasDedicatedWorkerUrl || options.Value.EnableLocalSandbox)
        {
            try
            {
                await DeployScopedPackageAsync(
                    brainId,
                    package,
                    assemblyBytes,
                    stateJson: null,
                    settingsJson: null,
                    allowedContracts: normalizedScope,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await DispatchCommandAsync(
                        brainId.Value,
                        package.Key,
                        command,
                        payloadJson,
                        cancellationToken).ConfigureAwait(false);
                    var fallbackTarget = hasDedicatedWorkerUrl
                        ? "cloud-sandbox"
                        : "local-unit-host";
                    return result with { ExecutionTarget = fallbackTarget, ExecutedByMachineId = null };
                }
                finally
                {
                    await UnloadAsync(brainId.Value, package.Key, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!hasDedicatedWorkerUrl
                && IsLocalSandboxUnavailable(error)
                && acaFallback is { IsConfigured: true }
                && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    error,
                    "Local desktop sandbox (--unit-host) unavailable for '{BrainId}/{AppKey}'; falling back to ACA Dynamic Sessions.",
                    brainId.Value,
                    package.Key);
            }
        }

        // Tier 3: Headless ACA Dynamic Sessions (Kata MicroVMs) fallback when configured; fail closed otherwise.
        if (acaFallback is { IsConfigured: true })
        {
            return await acaFallback.ExecuteScopedCommandAsync(
                brainId,
                package,
                assemblyBytes,
                command,
                payloadJson,
                normalizedScope,
                cancellationToken).ConfigureAwait(false);
        }

        throw new AcaDynamicSessionsNotConfiguredException(
            "Local desktop sandbox is disabled or unavailable and ACA Dynamic Sessions PoolManagementEndpoint is not configured.");
    }

    private static bool IsLocalSandboxUnavailable(Exception error)
        => error is FileNotFoundException
            or DirectoryNotFoundException
            or System.ComponentModel.Win32Exception;

    async Task<ConnectedSandboxResponse> ILocalSandboxExecutor.ExecuteAsync(
        ConnectedSandboxRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BrainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppToken);
        ct.ThrowIfCancellationRequested();

        var machineId = services.GetService<MachineId>()?.Value;
        var deployFrame = await SendToUnitHostAsync(
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
                    request.KernelCallbackUrl,
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
            var dispatchFrame = await SendToUnitHostAsync(
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
                        KernelUrl: request.KernelCallbackUrl,
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
            if (_unitHost is { } activeHost && !activeHost.HasExited)
            {
                try
                {
                    _ = await activeHost.SendAsync(
                        new UnitHostFrameRequest(
                            Id: NewFrameId(),
                            Op: "unload",
                            Unload: new UnloadWorkerRequest(request.BrainId, request.AppKey)),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogSwallowed(error, $"Local --unit-host unload failed for connected sandbox job '{request.JobId}'.");
                }
            }
        }
    }

    private static BrainId ParseBrain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.StartsWith('@') ? BrainId.System(value[1..]) : new BrainId(value);
    }

    private async Task<SandboxDeploymentResult> DeployPackageCoreAsync(
        BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        IReadOnlyList<string>? allowedContracts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var brainValue = brainId.Value;
        var key = DeploymentKey(brainValue, package.Key);
        var normalizedScope = allowedContracts is null
            ? null
            : IntoChat.OS.Host.Surface.NeuronContractDiscovery.NormalizeScopeList(allowedContracts);
        var token = normalizedScope is null
            ? tokens.Issue(brainValue, package.Key)
            : tokens.IssueScoped(brainId, package.Key, normalizedScope);
        var kernelUrl = ResolveKernelCallbackUrl();
        var request = new DeployWorkerRequest(
            brainValue,
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
                    $"Sandbox --unit-host deploy failed for '{brainValue}/{package.Key}': {frame.Error ?? "Empty deploy response."}");
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
            brainValue,
            package.Key,
            package,
            response.Signals,
            response.Schedules,
            [.. response.Commands.Select(c => c.Name)],
            response.StateJson ?? stateJson,
            settingsJson,
            parsedView,
            normalizedScope);
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

        var token = existing?.AllowedContracts is { } scopedContracts
            ? tokens.IssueScoped(ParseBrain(brainId), appKey, scopedContracts)
            : tokens.Issue(brainId, appKey);
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

    internal string? ResolveKernelCallbackUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.KernelCallbackUrl))
        {
            return options.Value.KernelCallbackUrl.Trim().TrimEnd('/');
        }

        // In external container mode, the container orchestrator (Aspire / compose) supplies
        // SANDBOX_KERNEL_URL pointing to the container-reachable tunnel endpoint; overriding it
        // with Kernel's internal loopback Kestrel address breaks callbacks from Linux containers.
        if (!string.IsNullOrWhiteSpace(options.Value.Url))
        {
            return null;
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
            var appRef = IntoChat.OS.Host.Primitives.AppRef.TryParse(brainId, entry.AppKey, out var parsed)
                ? parsed
                : IntoChat.OS.Host.Primitives.AppRef.Of(brainId, entry.AppKey);
            var identityClient = services.GetService<IntoChat.OS.Modules.Identity.Contracts.Accounts.IIdentityClient>();
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

    private static string DeploymentKey(string brainId, string appKey)
        => new DigitalBrain.NeuronId(brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId), "app", appKey).FullKey();

}
