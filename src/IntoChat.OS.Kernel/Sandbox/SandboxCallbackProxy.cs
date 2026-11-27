using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Proxies stdio pipe callbacks emitted by a confined <c>IntoChat.OS.Sandbox --unit-host</c> child process
/// either to the local <see cref="SandboxEdgeEndpoints"/> pipeline or to an SSRF-vetted remote Kernel edge
/// origin registered for an active connected-compute job.
/// </summary>
internal sealed class SandboxCallbackProxy(
    IOptions<SandboxOptions> options,
    AppTokenService tokens,
    IServiceProvider services,
    Func<string, string, string, CancellationToken, Task> forwardSignalAsync,
    Func<string, string, string> deploymentKeyFactory,
    ILogger logger,
    Func<HttpClient>? remoteCallbackClientFactory = null) : IDisposable
{
    private readonly Lock _remoteCallbackLock = new();
    private readonly Dictionary<string, List<RemoteCallbackRegistration>> _remoteCallbacks = new(StringComparer.Ordinal);
    private HttpClient? _remoteCallbackClient;

    private sealed record RemoteCallbackRegistration(Guid Id, string? CanonicalOrigin, string? RejectionReason);

    internal Guid? RegisterForRemoteJob(
        string deploymentKey,
        string jobId,
        string brainId,
        string appKey,
        string? kernelCallbackUrl)
    {
        if (string.IsNullOrWhiteSpace(kernelCallbackUrl)
            || kernelCallbackUrl.TrimStart().StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            kernelCallbackUrl,
            options.Value.AllowLoopbackKernelCallback,
            out var canonicalOrigin,
            out var rejectionReason))
        {
            return RegisterRemoteCallback(deploymentKey, canonicalOrigin, rejectionReason: null);
        }

        logger.LogWarning(
            "Rejected remote KernelCallbackUrl for connected sandbox job '{JobId}' ({BrainId}/{AppKey}): {Reason}",
            jobId,
            brainId,
            appKey,
            rejectionReason);
        return RegisterRemoteCallback(
            deploymentKey,
            canonicalOrigin: null,
            rejectionReason: rejectionReason ?? "Rejected KernelCallbackUrl.");
    }

    internal Guid RegisterRemoteCallback(string deploymentKey, string? canonicalOrigin, string? rejectionReason)
    {
        var id = Guid.NewGuid();
        var registration = new RemoteCallbackRegistration(id, canonicalOrigin, rejectionReason);
        lock (_remoteCallbackLock)
        {
            if (!_remoteCallbacks.TryGetValue(deploymentKey, out var list))
            {
                list = [];
                _remoteCallbacks[deploymentKey] = list;
            }

            list.Add(registration);
        }

        return id;
    }

    internal void UnregisterRemoteCallback(string deploymentKey, Guid registrationId)
    {
        lock (_remoteCallbackLock)
        {
            if (!_remoteCallbacks.TryGetValue(deploymentKey, out var list))
            {
                return;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Id == registrationId)
                {
                    list.RemoveAt(i);
                    break;
                }
            }

            if (list.Count == 0)
            {
                _remoteCallbacks.Remove(deploymentKey);
            }
        }
    }

    internal bool TryGetActiveRemoteCallback(string deploymentKey, out (string? CanonicalOrigin, string? RejectionReason) active)
    {
        lock (_remoteCallbackLock)
        {
            if (_remoteCallbacks.TryGetValue(deploymentKey, out var list) && list.Count > 0)
            {
                var latest = list[^1];
                active = (latest.CanonicalOrigin, latest.RejectionReason);
                return true;
            }
        }

        active = default;
        return false;
    }

    internal async Task<UnitHostFrameRequest> HandlePipeCallbackAsync(UnitHostFrameResponse frame, CancellationToken cancellationToken)
    {
        var kind = frame.CallbackKind ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(frame.BrainId)
            && !string.IsNullOrWhiteSpace(frame.AppKey)
            && TryGetActiveRemoteCallback(deploymentKeyFactory(frame.BrainId, frame.AppKey), out var registration))
        {
            if (string.IsNullOrWhiteSpace(registration.CanonicalOrigin))
            {
                var reason = registration.RejectionReason ?? "Remote KernelCallbackUrl was rejected by SSRF policy.";
                logger.LogWarning(
                    "Blocked sandbox callback '{CallbackKind}' for '{BrainId}/{AppKey}': {Reason}",
                    kind,
                    frame.BrainId,
                    frame.AppKey,
                    reason);
                return new UnitHostFrameRequest(
                    frame.Id,
                    "callback-reply",
                    Ok: false,
                    StatusCode: StatusCodes.Status403Forbidden,
                    InvokeResult: string.Equals(kind, "invoke", StringComparison.OrdinalIgnoreCase)
                        ? new SandboxEdgeEndpoints.EdgeInvokeResponse(null, reason)
                        : null,
                    Error: reason);
            }

            return await ForwardPipeCallbackToRemoteEdgeAsync(registration.CanonicalOrigin, kind, frame, cancellationToken).ConfigureAwait(false);
        }

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

            await forwardSignalAsync(signalIdentity.BrainId, frame.Signal.SignalName, frame.Signal.PayloadJson, cancellationToken)
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

    public void Dispose()
    {
        var callbackClient = Interlocked.Exchange(ref _remoteCallbackClient, null);
        callbackClient?.Dispose();
    }

    private HttpClient GetOrCreateRemoteCallbackClient()
    {
        var existing = Volatile.Read(ref _remoteCallbackClient);
        if (existing is not null)
        {
            return existing;
        }

        var created = remoteCallbackClientFactory is not null
            ? remoteCallbackClientFactory()
            : new HttpClient(
                SandboxCallbackUrlPolicy.CreateRemoteCallbackHandler(options.Value.AllowLoopbackKernelCallback),
                disposeHandler: true);
        var prior = Interlocked.CompareExchange(ref _remoteCallbackClient, created, null);
        if (prior is not null)
        {
            created.Dispose();
            return prior;
        }

        return created;
    }

    private async Task<UnitHostFrameRequest> ForwardPipeCallbackToRemoteEdgeAsync(
        string remoteCanonicalOrigin,
        string kind,
        UnitHostFrameResponse frame,
        CancellationToken cancellationToken)
    {
        string path;
        HttpContent? content;
        if (string.Equals(kind, "invoke", StringComparison.OrdinalIgnoreCase))
        {
            path = "/edge/sandbox/invoke";
            content = frame.Invoke is not null
                ? JsonContent.Create(frame.Invoke, SandboxEdgeJsonContext.Default.EdgeInvokeRequest)
                : null;
        }
        else if (string.Equals(kind, "signal", StringComparison.OrdinalIgnoreCase))
        {
            path = "/edge/sandbox/signals";
            content = frame.Signal is not null
                ? JsonContent.Create(frame.Signal, SandboxEdgeJsonContext.Default.EdgeSignalRequest)
                : null;
        }
        else if (string.Equals(kind, "log", StringComparison.OrdinalIgnoreCase))
        {
            path = "/edge/sandbox/logs";
            content = frame.Log is not null
                ? JsonContent.Create(frame.Log, SandboxEdgeJsonContext.Default.EdgeLogRequest)
                : null;
        }
        else
        {
            return new UnitHostFrameRequest(
                frame.Id,
                "callback-reply",
                Ok: false,
                StatusCode: StatusCodes.Status404NotFound,
                Error: $"Unsupported callback '{kind}'.");
        }

        if (content is null)
        {
            return new UnitHostFrameRequest(
                frame.Id,
                "callback-reply",
                Ok: false,
                StatusCode: StatusCodes.Status422UnprocessableEntity,
                Error: $"Callback '{kind}' payload is required.");
        }

        if (!SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            remoteCanonicalOrigin,
            options.Value.AllowLoopbackKernelCallback,
            out var verifiedOrigin,
            out var rejectionReason))
        {
            content.Dispose();
            var reason = rejectionReason ?? "Remote KernelCallbackUrl rejected by SSRF policy.";
            logger.LogWarning(
                "Blocked sandbox callback '{CallbackKind}' for '{BrainId}/{AppKey}': {Reason}",
                kind,
                frame.BrainId,
                frame.AppKey,
                reason);
            return new UnitHostFrameRequest(
                frame.Id,
                "callback-reply",
                Ok: false,
                StatusCode: StatusCodes.Status403Forbidden,
                InvokeResult: string.Equals(kind, "invoke", StringComparison.OrdinalIgnoreCase)
                    ? new SandboxEdgeEndpoints.EdgeInvokeResponse(null, reason)
                    : null,
                Error: reason);
        }

        var endpoint = new Uri(verifiedOrigin + path, UriKind.Absolute);
        var client = GetOrCreateRemoteCallbackClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content,
        };
        if (!string.IsNullOrWhiteSpace(frame.AppToken))
        {
            message.Headers.TryAddWithoutValidation("X-App-Token", frame.AppToken);
        }

        if (!string.IsNullOrWhiteSpace(frame.BrainId))
        {
            message.Headers.TryAddWithoutValidation("X-Brain-Id", frame.BrainId);
        }

        if (!string.IsNullOrWhiteSpace(frame.AppKey))
        {
            message.Headers.TryAddWithoutValidation("X-App-Key", frame.AppKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DigitalBrain.Capabilities.Http.HostNotAllowedException
            || ex is HttpRequestException { InnerException: DigitalBrain.Capabilities.Http.HostNotAllowedException })
        {
            var blocked = ex as DigitalBrain.Capabilities.Http.HostNotAllowedException
                ?? (DigitalBrain.Capabilities.Http.HostNotAllowedException)ex.InnerException!;
            logger.LogWarning(
                blocked,
                "Blocked sandbox callback '{CallbackKind}' for '{BrainId}/{AppKey}' at DNS/connect vetting.",
                kind,
                frame.BrainId,
                frame.AppKey);
            return new UnitHostFrameRequest(
                frame.Id,
                "callback-reply",
                Ok: false,
                StatusCode: StatusCodes.Status403Forbidden,
                InvokeResult: string.Equals(kind, "invoke", StringComparison.OrdinalIgnoreCase)
                    ? new SandboxEdgeEndpoints.EdgeInvokeResponse(null, blocked.Message)
                    : null,
                Error: blocked.Message);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.Equals(kind, "invoke", StringComparison.OrdinalIgnoreCase))
            {
                SandboxEdgeEndpoints.EdgeInvokeResponse? invokeResult = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        invokeResult = JsonSerializer.Deserialize(body, SandboxEdgeJsonContext.Default.EdgeInvokeResponse);
                    }
                    catch (JsonException)
                    {
                        invokeResult = new SandboxEdgeEndpoints.EdgeInvokeResponse(null, body);
                    }
                }

                invokeResult ??= new SandboxEdgeEndpoints.EdgeInvokeResponse(
                    null,
                    response.IsSuccessStatusCode ? null : $"Remote edge returned HTTP {statusCode}.");

                return new UnitHostFrameRequest(
                    frame.Id,
                    "callback-reply",
                    Ok: response.IsSuccessStatusCode,
                    StatusCode: statusCode,
                    InvokeResult: invokeResult,
                    Error: response.IsSuccessStatusCode ? null : invokeResult.Error);
            }

            return new UnitHostFrameRequest(
                frame.Id,
                "callback-reply",
                Ok: response.IsSuccessStatusCode,
                StatusCode: statusCode,
                Error: response.IsSuccessStatusCode ? null : (string.IsNullOrWhiteSpace(body) ? $"Remote edge returned HTTP {statusCode}." : body));
        }
    }
}
