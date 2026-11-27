using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// AOT-safe HTTP client for communicating with a remote <c>IntoChat.OS.Sandbox</c> worker container.
/// Uses source-generated <see cref="SandboxEdgeJsonContext"/> metadata for all request/response payloads.
/// </summary>
internal sealed class HttpSandboxWorkerClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SandboxOptions> options,
    IServiceProvider services,
    ILogger logger,
    int maxAttempts = HttpSandboxWorkerClient.DefaultMaxAttempts,
    TimeSpan? retryDelay = null)
{
    internal const int DefaultMaxAttempts = 20;

    private readonly int _maxAttempts = Math.Max(1, maxAttempts);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);

    public Task<DeployWorkerResponse> DeployAsync(
        string workerBaseUrl,
        DeployWorkerRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync(
            workerBaseUrl,
            "/apps/deploy",
            request,
            SandboxEdgeJsonContext.Default.DeployWorkerRequest,
            SandboxEdgeJsonContext.Default.DeployWorkerResponse,
            cancellationToken);

    public Task<DispatchWorkerResponse> DispatchAsync(
        string workerBaseUrl,
        DispatchWorkerRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync(
            workerBaseUrl,
            "/apps/dispatch",
            request,
            SandboxEdgeJsonContext.Default.DispatchWorkerRequest,
            SandboxEdgeJsonContext.Default.DispatchWorkerResponse,
            cancellationToken);

    public Task<UnloadWorkerResponse> UnloadAsync(
        string workerBaseUrl,
        UnloadWorkerRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync(
            workerBaseUrl,
            "/apps/unload",
            request,
            SandboxEdgeJsonContext.Default.UnloadWorkerRequest,
            SandboxEdgeJsonContext.Default.UnloadWorkerResponse,
            cancellationToken);

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

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string workerBaseUrl,
        string path,
        TRequest payload,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(workerBaseUrl.TrimEnd('/') + path, UriKind.Absolute);
        var client = httpClientFactory.CreateClient("SandboxWorker");

        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload, requestTypeInfo),
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
            catch (HttpRequestException error) when (attempt < _maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    error,
                    "Sandbox worker '{Path}' not reachable yet (attempt {Attempt}/{MaxAttempts}); retrying.",
                    path,
                    attempt,
                    _maxAttempts);
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
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

                var parsed = await response.Content.ReadFromJsonAsync(responseTypeInfo, cancellationToken)
                    .ConfigureAwait(false);
                return parsed ?? throw new InvalidOperationException($"Sandbox worker '{path}' returned an empty body.");
            }
        }
    }
}

internal sealed record WorkerCommandDescriptor(string Name, string Description);

internal sealed record DeployWorkerRequest(
    string BrainId,
    string AppKey,
    string Version,
    string Digest,
    string AssemblyBase64,
    string? StateJson,
    string? SettingsJson,
    string? KernelUrl,
    string? AppToken);

internal sealed record DeployWorkerResponse(
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

internal sealed record DispatchWorkerRequest(
    string BrainId,
    string AppKey,
    string Kind,
    string? Name,
    string? PayloadJson,
    string? StateJson,
    string? SettingsJson,
    string? KernelUrl,
    string? AppToken);

internal sealed record DispatchWorkerResponse(
    bool Handled,
    string? ResultJson,
    string? UpdatedStateJson,
    string? UpdatedViewJson,
    string? Error);

internal sealed record UnloadWorkerRequest(string BrainId, string AppKey);

internal sealed record UnloadWorkerResponse(bool Unloaded, string BrainId, string AppKey);
