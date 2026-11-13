using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// HTTP bridge between <see cref="AppNeuron"/> and the hardened container worker (<c>IntoChat.OS.Sandbox</c>).
/// Falls back to an in-memory tracking state when <see cref="SandboxOptions.Url"/> is not configured.
/// </summary>
internal sealed class HttpSandboxRuntimeBridge(
    IHttpClientFactory httpClientFactory,
    IOptions<SandboxOptions> options,
    AppTokenService tokens,
    IServiceProvider services,
    ILogger<HttpSandboxRuntimeBridge> logger) : ISandboxRuntimeBridge
{
    private readonly ConcurrentDictionary<string, DeployedAppEntry> _deployments = new(StringComparer.Ordinal);

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
        cancellationToken.ThrowIfCancellationRequested();

        var key = DeploymentKey(brainId, package.Key);
        var workerUrl = options.Value.Url;
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            var localView = new View(package.Title, new Column([new Text(package.Summary)]));
            _deployments[key] = new DeployedAppEntry(
                brainId,
                package.Key,
                package,
                package.Needs.Signals,
                package.Needs.Schedules,
                [.. package.Needs.Commands.Select(c => c.Name)],
                stateJson,
                settingsJson,
                localView);

            return new SandboxDeploymentResult(
                Succeeded: true,
                Error: null,
                package.Needs.Signals,
                package.Needs.Schedules,
                [.. package.Needs.Commands.Select(c => c.Name)],
                localView);
        }

        var token = tokens.Issue(brainId, package.Key);
        var request = new DeployWorkerRequest(
            brainId,
            package.Key,
            package.Version,
            package.Digest,
            Convert.ToBase64String(assemblyBytes),
            stateJson,
            settingsJson,
            string.IsNullOrWhiteSpace(options.Value.KernelCallbackUrl) ? null : options.Value.KernelCallbackUrl,
            token);

        var response = await PostToWorkerAsync<DeployWorkerRequest, DeployWorkerResponse>(
            workerUrl,
            "/apps/deploy",
            request,
            cancellationToken).ConfigureAwait(false);

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

        if (string.IsNullOrWhiteSpace(options.Value.Url))
        {
            return entry.LatestView;
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

        var workerUrl = options.Value.Url;
        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            return new SandboxDispatchResult(Handled: false, null, null, null, "External sandbox URL is not configured.");
        }

        var key = DeploymentKey(brainId, appKey);
        _deployments.TryGetValue(key, out var existing);

        var token = tokens.Issue(brainId, appKey);
        var request = new DispatchWorkerRequest(
            brainId,
            appKey,
            kind,
            name,
            payloadJson,
            existing?.StateJson,
            existing?.SettingsJson,
            string.IsNullOrWhiteSpace(options.Value.KernelCallbackUrl) ? null : options.Value.KernelCallbackUrl,
            token);

        var response = await PostToWorkerAsync<DispatchWorkerRequest, DispatchWorkerResponse>(
            workerUrl,
            "/apps/dispatch",
            request,
            cancellationToken).ConfigureAwait(false);

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

    private const int MaxWorkerAttempts = 20;

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
        if (view is null)
        {
            return;
        }

        try
        {
            var generation = entry.NextViewGeneration();
            using (InProcessCallerScope.EnterKernel(entry.BrainId))
            {
                var viewId = new NeuronId(new BrainId(entry.BrainId), IntoChat.OS.Modules.Flutter.UIVocabulary.ViewType, $"apps/{entry.AppKey}/view");
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

    private static string DeploymentKey(string brainId, string appKey) => $"{brainId}/{appKey}";

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
}
