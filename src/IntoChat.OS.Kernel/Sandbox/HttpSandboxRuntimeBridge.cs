using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Runtime bridge between <see cref="AppNeuron"/> / <c>CodeOrchestrationSynapse</c> and the isolated
/// <c>IntoChat.OS.Sandbox</c> worker. For scoped single-shot code orchestration, routes deterministically:
/// 1. Owner's online consented <see cref="ICodeSandbox"/> connected client via <see cref="IConnectedComputeRouter"/>
///    (&lt;500 ms unavailability detection),
/// 2. Dedicated cloud sandbox Container App (<see cref="SandboxOptions.Url"/>) or environment-scrubbed local
///    <c>IntoChat.OS.Sandbox --unit-host</c> child process when <see cref="SandboxOptions.EnableLocalSandbox"/> is enabled.
/// </summary>
internal sealed class HttpSandboxRuntimeBridge : ISandboxRuntimeBridge, ILocalSandboxExecutor, IAsyncDisposable
{
    private readonly IOptions<SandboxOptions> _options;
    private readonly AppTokenService _tokens;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpSandboxRuntimeBridge> _logger;
    private readonly IConnectedComputeRouter? _connectedComputeRouter;
    private readonly HttpSandboxWorkerClient _workerClient;
    private readonly SandboxCallbackProxy _callbackProxy;
    private readonly UnitHostTransport _unitHostTransport;
    private readonly ConcurrentDictionary<string, DeployedAppEntry> _deployments = new(StringComparer.Ordinal);
    private int _disposed;

    public HttpSandboxRuntimeBridge(
        IHttpClientFactory httpClientFactory,
        IOptions<SandboxOptions> options,
        AppTokenService tokens,
        IServiceProvider services,
        ILogger<HttpSandboxRuntimeBridge> logger,
        IConnectedComputeRouter? connectedComputeRouter = null,
        IConfinedProcessRunner? confinedProcessRunner = null)
    {
        _options = options;
        _tokens = tokens;
        _services = services;
        _logger = logger;
        _connectedComputeRouter = connectedComputeRouter;
        _workerClient = new HttpSandboxWorkerClient(httpClientFactory, options, services, logger);
        _callbackProxy = new SandboxCallbackProxy(options, tokens, services, ForwardSignalAsync, DeploymentKey, logger);
        _unitHostTransport = new UnitHostTransport(options, services, _callbackProxy, logger, confinedProcessRunner);
    }

    internal ConfinementMode? ActiveUnitHostConfinementMode => _unitHostTransport.ActiveConfinementMode;

    internal string? ActiveUnitHostTempDirectory => _unitHostTransport.ActiveTempDirectory;

    internal string? ActiveUnitHostWorkingDirectory => _unitHostTransport.ActiveWorkingDirectory;

    internal Task<IReadOnlyDictionary<string, string>> ProbeUnitHostEnvironmentAsync(CancellationToken cancellationToken = default)
        => _unitHostTransport.ProbeEnvironmentAsync(cancellationToken);

    internal IConfinedProcessRunner ResolveConfinedProcessRunner()
        => _unitHostTransport.ResolveConfinedProcessRunner();

    internal Guid RegisterRemoteCallback(string deploymentKey, string? canonicalOrigin, string? rejectionReason)
        => _callbackProxy.RegisterRemoteCallback(deploymentKey, canonicalOrigin, rejectionReason);

    internal void UnregisterRemoteCallback(string deploymentKey, Guid registrationId)
        => _callbackProxy.UnregisterRemoteCallback(deploymentKey, registrationId);

    internal bool TryGetActiveRemoteCallback(string deploymentKey, out (string? CanonicalOrigin, string? RejectionReason) active)
        => _callbackProxy.TryGetActiveRemoteCallback(deploymentKey, out active);

    internal string? ResolveKernelCallbackUrl()
        => _workerClient.ResolveKernelCallbackUrl();

    internal static ConfinementSpec BuildUnitHostConfinementSpec(
        UnitHostLaunchCommand command,
        string assemblyPath,
        string contractsDirectory,
        string sessionWorkingDirectory,
        string? sandboxId = null)
        => UnitHostTransport.BuildConfinementSpec(command, assemblyPath, contractsDirectory, sessionWorkingDirectory, sandboxId);

    internal static UnitHostLaunchCommand ResolveUnitHostLaunchCommand(
        string assemblyPath,
        string contractsDirectory,
        bool? isWindows = null)
        => UnitHostTransport.ResolveLaunchCommand(assemblyPath, contractsDirectory, isWindows);

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
        var router = _connectedComputeRouter ?? _services.GetService<IConnectedComputeRouter>();
        if (router is not null)
        {
            var scopedToken = _tokens.IssueScoped(brainId, package.Key, normalizedScope);
            var kernelCallbackUrl = _workerClient.ResolveKernelCallbackUrl();
            var remoteRequest = new ConnectedSandboxRequest(
                JobId: UnitHostTransport.NewFrameId(),
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
                _logger.LogInformation(
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

            _logger.LogDebug(
                "No online consented ICodeSandbox connected client answered within availability budget for '{BrainId}/{AppKey}'; proceeding to fallback routing.",
                brainId.Value,
                package.Key);
        }

        var hasDedicatedWorkerUrl = !string.IsNullOrWhiteSpace(_options.Value.Url);

        // Tier 2: Dedicated sandbox worker URL or local desktop sandbox (--unit-host) when enabled.
        if (hasDedicatedWorkerUrl || _options.Value.EnableLocalSandbox)
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

        throw new InvalidOperationException(
            "Local desktop sandbox is disabled and no dedicated Sandbox:Url worker is configured.");
    }

    Task<ConnectedSandboxResponse> ILocalSandboxExecutor.ExecuteAsync(
        ConnectedSandboxRequest request,
        CancellationToken ct)
        => _unitHostTransport.ExecuteLocalAsync(request, DeploymentKey, ct);

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
                _logger.LogWarning(
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

        var workerUrl = _options.Value.Url;
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            await _unitHostTransport.TryUnloadAsync(
                brainId,
                appKey,
                $"Sandbox --unit-host unload failed for '{brainId}/{appKey}'.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            _ = await _workerClient.UnloadAsync(
                workerUrl,
                new UnloadWorkerRequest(brainId, appKey),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, $"Sandbox unload failed for '{brainId}/{appKey}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _unitHostTransport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _callbackProxy.Dispose();
        }
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
            ? _tokens.Issue(brainValue, package.Key)
            : _tokens.IssueScoped(brainId, package.Key, normalizedScope);
        var workerUrl = _options.Value.Url;
        var kernelUrl = string.IsNullOrWhiteSpace(workerUrl) ? "pipe://edge" : _workerClient.ResolveKernelCallbackUrl();
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
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            var frame = await _unitHostTransport.SendAsync(
                new UnitHostFrameRequest(
                    Id: UnitHostTransport.NewFrameId(),
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
            response = await _workerClient.DeployAsync(workerUrl, request, cancellationToken).ConfigureAwait(false);
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
            ? _tokens.IssueScoped(ParseBrain(brainId), appKey, scopedContracts)
            : _tokens.Issue(brainId, appKey);
        var workerUrl = _options.Value.Url;
        var kernelUrl = string.IsNullOrWhiteSpace(workerUrl) ? "pipe://edge" : _workerClient.ResolveKernelCallbackUrl();
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
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            var frame = await _unitHostTransport.SendAsync(
                new UnitHostFrameRequest(
                    Id: UnitHostTransport.NewFrameId(),
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
            response = await _workerClient.DispatchAsync(workerUrl, request, cancellationToken).ConfigureAwait(false);
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

    private async Task PublishViewAsync(DeployedAppEntry entry, View? view, CancellationToken cancellationToken)
    {
        if (view is null || _services.GetService<Orleans.IGrainFactory>() is null)
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
            var identityClient = _services.GetService<IntoChat.OS.Modules.Identity.Contracts.Accounts.IIdentityClient>();
            var owner = identityClient is not null
                ? await identityClient.OwnerOfBrain(brainId.Value).ConfigureAwait(false)
                : null;
            var resolvedAccount = string.IsNullOrWhiteSpace(owner?.AccountId) ? brainId.Value : owner.AccountId;
            var appPrincipal = DigitalBrain.Capabilities.AppPrincipal.Id(appRef.Key);
            var edge = _services.GetService<DigitalBrain.Access.EdgeAuthority>();
            using (edge?.EnterInProcess())
            using (edge?.Enter(edge.App(appRef.Key, new DigitalBrain.Access.Actor.Person(appPrincipal, resolvedAccount), brainId)))
            {
                var viewId = new NeuronId(brainId, IntoChat.OS.Modules.Flutter.UIVocabulary.ViewType, appRef.ViewPath);
                await SiloNeurons.Get<IView>(_services, viewId).Publish(generation, view).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogSwallowed(error, $"Optional UI view publication skipped for '{entry.BrainId}/{entry.AppKey}'.");
        }
    }

    private static View? DeserializeView(string? viewJson)
        => string.IsNullOrWhiteSpace(viewJson)
            ? null
            : JsonSerializer.Deserialize(viewJson, SandboxEdgeJsonContext.Default.View);

    private static BrainId ParseBrain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.StartsWith('@') ? BrainId.System(value[1..]) : new BrainId(value);
    }

    private static string DeploymentKey(string brainId, string appKey)
        => new NeuronId(brainId is ['@', .. var sys] ? BrainId.System(sys) : new BrainId(brainId), "app", appKey).FullKey();

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
}
