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
using IntoChat.OS.Modules.Apps;
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
/// Runtime bridge between <see cref="AppNeuron"/> and the isolated <c>IntoChat.OS.Sandbox</c> worker.
/// Routes to the remote Docker worker when <see cref="SandboxOptions.Url"/> is configured, and
/// executes out-of-process in an environment-scrubbed <c>IntoChat.OS.Sandbox --unit-host</c> child
/// process over NDJSON stdio pipes otherwise.
/// </summary>
internal sealed partial class HttpSandboxRuntimeBridge(
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

    internal string? ResolveKernelCallbackUrl()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.KernelCallbackUrl))
        {
            return options.Value.KernelCallbackUrl!;
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

    private static string DeploymentKey(string brainId, string appKey)
        => new DigitalBrain.NeuronId(brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId), "app", appKey).FullKey();

}
