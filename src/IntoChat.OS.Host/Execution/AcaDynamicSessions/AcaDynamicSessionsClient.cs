using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Host.Execution;

/// <summary>Wire request sent to the ACA Dynamic Sessions <c>/executions</c> management plane endpoint.</summary>
public sealed record AcaExecutionRequest(
    string Mode,
    string Action,
    int TimeoutInSeconds,
    string? BrainId = null,
    string? AppKey = null,
    string? Version = null,
    string? Digest = null,
    string? AssemblyBase64 = null,
    string? Command = null,
    string? PayloadJson = null,
    string[]? AllowedContracts = null,
    string? KernelCallbackUrl = null,
    string? AppToken = null,
    string? CandidateSha = null,
    string[]? ChangedFiles = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Normalized result returned from an ACA Dynamic Sessions <c>/executions</c> invocation.</summary>
public sealed record AcaExecutionResponse(
    string SessionId,
    string Status,
    bool Succeeded,
    bool Handled,
    string? ResultJson,
    string? UpdatedStateJson,
    string? Stdout,
    string? Stderr,
    string? Error,
    int? ExitCode,
    string? McpEndpoint,
    string? UiEndpoint);

/// <summary>Session descriptor returned by <c>GET /sessions</c> on the ACA Dynamic Sessions pool management plane.</summary>
public sealed record AcaSessionInfo(string SessionId, string? Status);

/// <summary>
/// Hardened HTTP client for the Azure Container Apps Dynamic Sessions pool management plane
/// (<c>poolManagementEndpoint</c>). Handles Entra/Managed Identity bearer token headers,
/// session-id isolation (<c>?identifier=...</c>), payload byte caps, strict linked timeouts,
/// fail-closed unconfigured behavior, and secret-redacted diagnostics.
/// </summary>
public sealed class AcaDynamicSessionsClient
{
    private const int MaxErrorBodyChars = 2048;

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Func<AcaDynamicSessionsOptions> _optionsAccessor;
    private readonly IAcaDynamicSessionsTokenProvider _tokenProvider;
    private readonly ILogger<AcaDynamicSessionsClient>? _logger;

    public AcaDynamicSessionsClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AcaDynamicSessionsOptions> options,
        IAcaDynamicSessionsTokenProvider tokenProvider,
        ILogger<AcaDynamicSessionsClient>? logger = null)
        : this(
            () => (httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory)))
                .CreateClient(AcaDynamicSessionsOptions.HttpClientName),
            () => (options ?? throw new ArgumentNullException(nameof(options))).Value,
            tokenProvider,
            logger)
    {
    }

    internal AcaDynamicSessionsClient(
        HttpClient httpClient,
        AcaDynamicSessionsOptions options,
        IAcaDynamicSessionsTokenProvider tokenProvider,
        ILogger<AcaDynamicSessionsClient>? logger = null)
        : this(
            () => httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            () => options ?? throw new ArgumentNullException(nameof(options)),
            tokenProvider,
            logger)
    {
    }

    private AcaDynamicSessionsClient(
        Func<HttpClient> httpClientFactory,
        Func<AcaDynamicSessionsOptions> optionsAccessor,
        IAcaDynamicSessionsTokenProvider tokenProvider,
        ILogger<AcaDynamicSessionsClient>? logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsAccessor = optionsAccessor;
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger;
    }

    public bool IsConfigured => _optionsAccessor().IsConfigured;

    public AcaDynamicSessionsOptions CurrentOptions => _optionsAccessor();

    public void EnsureConfigured()
    {
        var opts = _optionsAccessor();
        opts.Validate();
        _ = opts.GetValidatedEndpointUri();
    }

    /// <summary>
    /// Executes an isolated workload inside the Kata MicroVM bound to <paramref name="sessionId"/>
    /// via <c>POST {poolManagementEndpoint}/executions?identifier={sessionId}&amp;api-version={apiVersion}</c>.
    /// </summary>
    public async Task<AcaExecutionResponse> ExecuteAsync(
        string sessionId,
        AcaExecutionRequest request,
        TimeSpan? timeoutOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AcaSessionIsolation.ValidateSessionId(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _optionsAccessor();
        opts.Validate();
        var baseUri = opts.GetValidatedEndpointUri();

        var effectiveTimeout = timeoutOverride ?? opts.ExecutionTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveTimeout, TimeSpan.Zero);

        var envelope = new ExecutionEnvelope(
            Properties: new ExecutionProperties(
                CodeInputType: "inline",
                ExecutionType: "synchronous",
                TimeoutInSeconds: Math.Max(1, (int)Math.Ceiling(effectiveTimeout.TotalSeconds)),
                Mode: request.Mode,
                Action: request.Action,
                BrainId: request.BrainId,
                AppKey: request.AppKey,
                Version: request.Version,
                Digest: request.Digest,
                AssemblyBase64: request.AssemblyBase64,
                Command: request.Command,
                PayloadJson: request.PayloadJson,
                AllowedContracts: request.AllowedContracts,
                KernelCallbackUrl: request.KernelCallbackUrl,
                AppToken: request.AppToken,
                CandidateSha: request.CandidateSha,
                ChangedFiles: request.ChangedFiles,
                Environment: request.Environment));

        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, WireJson);
        if (bodyBytes.Length > opts.MaxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"ACA Dynamic Sessions execution payload ({bodyBytes.Length} bytes) exceeds MaxPayloadBytes ({opts.MaxPayloadBytes} bytes).");
        }

        var token = await AcquireBearerTokenAsync(opts, cancellationToken).ConfigureAwait(false);
        var endpoint = BuildSessionEndpointUri(baseUri, "/executions", sessionId, opts.ApiVersion);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName,
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.TryAddWithoutValidation("X-IntoChat-Session-Id", sessionId);

        using var response = await SendWithStrictTimeoutAsync(
            httpRequest,
            effectiveTimeout,
            sessionId,
            operation: "executions",
            knownSecrets: [token, request.AppToken],
            cancellationToken).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TruncateAndRedact(responseBody, token, request.AppToken);
            throw new AcaDynamicSessionsException(
                $"ACA Dynamic Sessions /executions for session '{sessionId}' returned HTTP {(int)response.StatusCode}: {detail}",
                response.StatusCode);
        }

        return ParseExecutionResponse(sessionId, responseBody, token, request.AppToken);
    }

    /// <summary>
    /// Probes the health of an active session via
    /// <c>GET {poolManagementEndpoint}/health?identifier={sessionId}&amp;api-version={apiVersion}</c>.
    /// </summary>
    public async Task<bool> CheckHealthAsync(
        string sessionId,
        TimeSpan? timeoutOverride = null,
        CancellationToken cancellationToken = default)
    {
        AcaSessionIsolation.ValidateSessionId(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _optionsAccessor();
        opts.Validate();
        var baseUri = opts.GetValidatedEndpointUri();
        var token = await AcquireBearerTokenAsync(opts, cancellationToken).ConfigureAwait(false);
        var endpoint = BuildSessionEndpointUri(baseUri, "/health", sessionId, opts.ApiVersion);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.TryAddWithoutValidation("X-IntoChat-Session-Id", sessionId);

        var timeout = timeoutOverride ?? TimeSpan.FromSeconds(5);
        using var response = await SendWithStrictTimeoutAsync(
            httpRequest,
            timeout,
            sessionId,
            operation: "health",
            knownSecrets: [token],
            cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Terminates an ephemeral Kata MicroVM session via
    /// <c>DELETE {poolManagementEndpoint}/sessions/{sessionId}?api-version={apiVersion}</c>.
    /// Treats 404 NotFound as already terminated.
    /// </summary>
    public async Task TerminateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        AcaSessionIsolation.ValidateSessionId(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _optionsAccessor();
        opts.Validate();
        var baseUri = opts.GetValidatedEndpointUri();
        var token = await AcquireBearerTokenAsync(opts, cancellationToken).ConfigureAwait(false);
        var endpoint = BuildPoolPathUri(
            baseUri,
            $"/sessions/{Uri.EscapeDataString(sessionId)}",
            opts.ApiVersion);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.TryAddWithoutValidation("X-IntoChat-Session-Id", sessionId);

        using var response = await SendWithStrictTimeoutAsync(
            httpRequest,
            TimeSpan.FromSeconds(15),
            sessionId,
            operation: "terminate-session",
            knownSecrets: [token],
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound || response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = TruncateAndRedact(responseBody, token);
        throw new AcaDynamicSessionsException(
            $"ACA Dynamic Sessions terminate for session '{sessionId}' returned HTTP {(int)response.StatusCode}: {detail}",
            response.StatusCode);
    }

    /// <summary>
    /// Lists active sessions in the pool via <c>GET {poolManagementEndpoint}/sessions?api-version={apiVersion}</c>.
    /// </summary>
    public async Task<AcaSessionInfo[]> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _optionsAccessor();
        opts.Validate();
        var baseUri = opts.GetValidatedEndpointUri();
        var token = await AcquireBearerTokenAsync(opts, cancellationToken).ConfigureAwait(false);
        var endpoint = BuildPoolPathUri(baseUri, "/sessions", opts.ApiVersion);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await SendWithStrictTimeoutAsync(
            httpRequest,
            TimeSpan.FromSeconds(15),
            sessionId: "pool",
            operation: "list-sessions",
            knownSecrets: [token],
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TruncateAndRedact(body, token);
            throw new AcaDynamicSessionsException(
                $"ACA Dynamic Sessions /sessions returned HTTP {(int)response.StatusCode}: {detail}",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.ValueKind == JsonValueKind.Object
            && (root.TryGetProperty("value", out items) || root.TryGetProperty("sessions", out items))
            && items.ValueKind == JsonValueKind.Array)
        {
            // Extracted array property.
        }
        else
        {
            return [];
        }

        var list = new List<AcaSessionInfo>();
        foreach (var element in items.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var id = element.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(new AcaSessionInfo(id, null));
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var id = TryGetString(element, "identifier")
                    ?? TryGetString(element, "sessionId")
                    ?? TryGetString(element, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(new AcaSessionInfo(id, TryGetString(element, "status")));
                }
            }
        }

        return [.. list];
    }

    public static Uri BuildSessionEndpointUri(Uri baseEndpoint, string relativePath, string sessionId, string apiVersion)
    {
        var prefix = baseEndpoint.AbsoluteUri.TrimEnd('/');
        var path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return new Uri(
            $"{prefix}{path}?identifier={Uri.EscapeDataString(sessionId)}&api-version={Uri.EscapeDataString(apiVersion)}",
            UriKind.Absolute);
    }

    public static Uri BuildPoolPathUri(Uri baseEndpoint, string relativePath, string apiVersion)
    {
        var prefix = baseEndpoint.AbsoluteUri.TrimEnd('/');
        var path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return new Uri(
            $"{prefix}{path}?api-version={Uri.EscapeDataString(apiVersion)}",
            UriKind.Absolute);
    }

    private async Task<string> AcquireBearerTokenAsync(AcaDynamicSessionsOptions opts, CancellationToken ct)
    {
        var token = await _tokenProvider.AcquireTokenAsync(opts.TokenScope, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new AcaDynamicSessionsException(
                "ACA Dynamic Sessions token provider returned an empty bearer token.");
        }

        return token.Trim();
    }

    private async Task<HttpResponseMessage> SendWithStrictTimeoutAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        string sessionId,
        string operation,
        string?[] knownSecrets,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var client = _httpClientFactory();
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogWarning(
                "ACA Dynamic Sessions '{Operation}' for session '{SessionId}' timed out after {TimeoutSeconds:F1}s.",
                operation,
                sessionId,
                timeout.TotalSeconds);
            throw new TimeoutException(
                $"ACA Dynamic Sessions '{operation}' for session '{sessionId}' timed out after {timeout.TotalSeconds:F1}s.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            var redacted = AcaPayloadSecretScrubber.RedactSecrets(ex.Message, knownSecrets);
            throw new AcaDynamicSessionsException(
                $"ACA Dynamic Sessions '{operation}' request failed for session '{sessionId}': {redacted}",
                ex.StatusCode,
                ex);
        }
    }

    private static AcaExecutionResponse ParseExecutionResponse(
        string sessionId,
        string body,
        string? bearerToken,
        string? appToken)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AcaExecutionResponse(
                SessionId: sessionId,
                Status: "Failed",
                Succeeded: false,
                Handled: false,
                ResultJson: null,
                UpdatedStateJson: null,
                Stdout: null,
                Stderr: null,
                Error: "ACA Dynamic Sessions returned an empty execution body.",
                ExitCode: null,
                McpEndpoint: null,
                UiEndpoint: null);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var props = root.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object
            ? propsEl
            : root;
        var resultObj = props.TryGetProperty("result", out var resEl) && resEl.ValueKind == JsonValueKind.Object
            ? resEl
            : (root.TryGetProperty("result", out var topResEl) && topResEl.ValueKind == JsonValueKind.Object ? topResEl : default);

        var status = TryGetString(props, "status") ?? TryGetString(root, "status") ?? "Succeeded";
        var stdout = TryGetString(resultObj, "stdout") ?? TryGetString(props, "stdout") ?? TryGetString(root, "stdout");
        var stderr = TryGetString(resultObj, "stderr") ?? TryGetString(props, "stderr") ?? TryGetString(root, "stderr");
        var rawError = TryGetString(resultObj, "error") ?? TryGetString(props, "error") ?? TryGetString(root, "error");
        var exitCode = TryGetInt(resultObj, "exitCode") ?? TryGetInt(props, "exitCode") ?? TryGetInt(root, "exitCode");

        var resultJson = TryGetStringOrRawJson(resultObj, "resultJson")
            ?? TryGetStringOrRawJson(props, "resultJson")
            ?? TryGetStringOrRawJson(root, "resultJson")
            ?? TryGetStringOrRawJson(resultObj, "executionResult")
            ?? TryGetStringOrRawJson(props, "executionResult")
            ?? TryGetStringOrRawJson(root, "executionResult");

        var updatedStateJson = TryGetStringOrRawJson(resultObj, "updatedStateJson")
            ?? TryGetStringOrRawJson(props, "updatedStateJson")
            ?? TryGetStringOrRawJson(root, "updatedStateJson");

        var handled = TryGetBool(resultObj, "handled")
            ?? TryGetBool(props, "handled")
            ?? TryGetBool(root, "handled")
            ?? (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(rawError));

        var succeeded = string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(rawError)
            && (exitCode is null or 0);

        var redactedError = string.IsNullOrWhiteSpace(rawError)
            ? (!succeeded && !string.IsNullOrWhiteSpace(stderr)
                ? AcaPayloadSecretScrubber.RedactSecrets(stderr, bearerToken, appToken)
                : null)
            : AcaPayloadSecretScrubber.RedactSecrets(rawError, bearerToken, appToken);

        var mcpEndpoint = TryGetString(resultObj, "mcpEndpoint")
            ?? TryGetString(props, "mcpEndpoint")
            ?? TryGetString(root, "mcpEndpoint");
        var uiEndpoint = TryGetString(resultObj, "uiEndpoint")
            ?? TryGetString(props, "uiEndpoint")
            ?? TryGetString(root, "uiEndpoint");

        return new AcaExecutionResponse(
            SessionId: sessionId,
            Status: status,
            Succeeded: succeeded,
            Handled: handled,
            ResultJson: resultJson,
            UpdatedStateJson: updatedStateJson,
            Stdout: stdout,
            Stderr: stderr,
            Error: redactedError,
            ExitCode: exitCode,
            McpEndpoint: mcpEndpoint,
            UiEndpoint: uiEndpoint);
    }

    private static string TruncateAndRedact(string? body, params string?[] knownSecrets)
    {
        var scrubbed = AcaPayloadSecretScrubber.RedactSecrets(body, knownSecrets);
        return scrubbed.Length <= MaxErrorBodyChars ? scrubbed : scrubbed[..MaxErrorBodyChars] + "...";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static string? TryGetStringOrRawJson(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => prop.GetRawText(),
        };
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return prop.GetBoolean();
        }

        return null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    private sealed record ExecutionEnvelope(ExecutionProperties Properties);

    private sealed record ExecutionProperties(
        string CodeInputType,
        string ExecutionType,
        int TimeoutInSeconds,
        string Mode,
        string Action,
        string? BrainId,
        string? AppKey,
        string? Version,
        string? Digest,
        string? AssemblyBase64,
        string? Command,
        string? PayloadJson,
        string[]? AllowedContracts,
        string? KernelCallbackUrl,
        string? AppToken,
        string? CandidateSha,
        string[]? ChangedFiles,
        IReadOnlyDictionary<string, string>? Environment);
}
