using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Acquires and caches Entra ID bearer tokens from the Azure Container Apps Managed Identity endpoint
/// (<c>IDENTITY_ENDPOINT</c> + <c>IDENTITY_HEADER</c>) with fallback to Azure IMDS
/// (<c>169.254.169.254</c>). Fails closed if no token endpoint responds with a valid token, and
/// never logs bearer token material.
/// </summary>
public sealed class ManagedIdentityAcaTokenProvider : IAcaDynamicSessionsTokenProvider, IDisposable
{
    private const string IdentityEndpointEnvVar = "IDENTITY_ENDPOINT";
    private const string IdentityHeaderEnvVar = "IDENTITY_HEADER";
    private const string LegacyMsiEndpointEnvVar = "MSI_ENDPOINT";
    private const string LegacyMsiSecretEnvVar = "MSI_SECRET";
    private const string AcaMsiApiVersion = "2019-08-01";
    private const string ImdsEndpoint = "http://169.254.169.254/metadata/identity/oauth2/token";
    private const string ImdsApiVersion = "2018-02-01";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<HttpClient> _httpClientFactory;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string?> _getClientId;
    private readonly TimeProvider _time;
    private readonly ILogger<ManagedIdentityAcaTokenProvider>? _logger;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private int _disposed;

    public ManagedIdentityAcaTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<AcaDynamicSessionsOptions>? options = null,
        TimeProvider? time = null,
        ILogger<ManagedIdentityAcaTokenProvider>? logger = null)
        : this(
            () => (httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory)))
                .CreateClient(AcaDynamicSessionsOptions.HttpClientName),
            Environment.GetEnvironmentVariable,
            () => options?.Value.ManagedIdentityClientId,
            time,
            logger)
    {
    }

    internal ManagedIdentityAcaTokenProvider(
        HttpClient httpClient,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string?>? getClientId = null,
        TimeProvider? time = null,
        ILogger<ManagedIdentityAcaTokenProvider>? logger = null)
        : this(
            () => httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            getEnvironmentVariable ?? Environment.GetEnvironmentVariable,
            getClientId ?? (() => null),
            time,
            logger)
    {
    }

    private ManagedIdentityAcaTokenProvider(
        Func<HttpClient> httpClientFactory,
        Func<string, string?> getEnvironmentVariable,
        Func<string?> getClientId,
        TimeProvider? time,
        ILogger<ManagedIdentityAcaTokenProvider>? logger)
    {
        _httpClientFactory = httpClientFactory;
        _getEnvironmentVariable = getEnvironmentVariable;
        _getClientId = getClientId;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<string> AcquireTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var resource = NormalizeResource(scope);
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(resource, out var existing) && existing.ExpiresAtUtc - RefreshMargin > now)
        {
            return existing.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            now = _time.GetUtcNow();
            if (_cache.TryGetValue(resource, out existing) && existing.ExpiresAtUtc - RefreshMargin > now)
            {
                return existing.AccessToken;
            }

            var acquired = await FetchTokenAsync(resource, cancellationToken).ConfigureAwait(false);
            _cache[resource] = acquired;
            return acquired.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _refreshLock.Dispose();
    }

    internal static string NormalizeResource(string scope)
    {
        var trimmed = scope.Trim();
        if (trimmed.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^"/.default".Length];
        }

        return trimmed.TrimEnd('/');
    }

    private async Task<CachedToken> FetchTokenAsync(string resource, CancellationToken cancellationToken)
    {
        using var request = BuildTokenRequest(resource);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AcquisitionTimeout);

        var client = _httpClientFactory();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                $"Timed out acquiring Managed Identity bearer token for ACA Dynamic Sessions resource '{resource}'.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AcaDynamicSessionsException(
                $"Failed to acquire Managed Identity bearer token for ACA Dynamic Sessions resource '{resource}': {AcaPayloadSecretScrubber.RedactSecrets(ex.Message)}",
                innerException: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var redacted = AcaPayloadSecretScrubber.RedactSecrets(body);
                _logger?.LogWarning(
                    "ACA Dynamic Sessions Managed Identity token endpoint returned HTTP {StatusCode}.",
                    (int)response.StatusCode);
                throw new AcaDynamicSessionsException(
                    $"Managed Identity token acquisition failed with HTTP {(int)response.StatusCode}: {redacted}",
                    response.StatusCode);
            }

            return ParseTokenResponse(body, _time.GetUtcNow());
        }
    }

    private HttpRequestMessage BuildTokenRequest(string resource)
    {
        var clientId = _getClientId();
        var identityEndpoint = _getEnvironmentVariable(IdentityEndpointEnvVar);
        var identityHeader = _getEnvironmentVariable(IdentityHeaderEnvVar);
        if (!string.IsNullOrWhiteSpace(identityEndpoint) && !string.IsNullOrWhiteSpace(identityHeader))
        {
            var uri = AppendQuery(identityEndpoint.Trim(), AcaMsiApiVersion, resource, clientId);
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("X-IDENTITY-HEADER", identityHeader);
            return req;
        }

        var legacyEndpoint = _getEnvironmentVariable(LegacyMsiEndpointEnvVar);
        var legacySecret = _getEnvironmentVariable(LegacyMsiSecretEnvVar);
        if (!string.IsNullOrWhiteSpace(legacyEndpoint) && !string.IsNullOrWhiteSpace(legacySecret))
        {
            var uri = AppendQuery(legacyEndpoint.Trim(), AcaMsiApiVersion, resource, clientId);
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("Secret", legacySecret);
            return req;
        }

        var imdsUri = AppendQuery(ImdsEndpoint, ImdsApiVersion, resource, clientId);
        var imdsReq = new HttpRequestMessage(HttpMethod.Get, imdsUri);
        imdsReq.Headers.TryAddWithoutValidation("Metadata", "true");
        return imdsReq;
    }

    private static Uri AppendQuery(string baseEndpoint, string apiVersion, string resource, string? clientId)
    {
        var separator = baseEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = $"{baseEndpoint}{separator}api-version={Uri.EscapeDataString(apiVersion)}&resource={Uri.EscapeDataString(resource)}";
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            url += $"&client_id={Uri.EscapeDataString(clientId.Trim())}";
        }

        return new Uri(url, UriKind.Absolute);
    }

    private static CachedToken ParseTokenResponse(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new AcaDynamicSessionsException("Managed Identity token endpoint returned an empty body.");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new AcaDynamicSessionsException("Managed Identity token response did not include a valid access_token.");
        }

        var token = tokenElement.GetString()!;
        var expiresAt = now.AddMinutes(10);

        if (root.TryGetProperty("expires_on", out var expiresOnEl))
        {
            if (expiresOnEl.ValueKind == JsonValueKind.Number && expiresOnEl.TryGetInt64(out var epochNum))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(epochNum);
            }
            else if (expiresOnEl.ValueKind == JsonValueKind.String)
            {
                var raw = expiresOnEl.GetString();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochStr))
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(epochStr);
                }
                else if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
                {
                    expiresAt = parsedDate.ToUniversalTime();
                }
            }
        }
        else if (root.TryGetProperty("expires_in", out var expiresInEl))
        {
            if (expiresInEl.ValueKind == JsonValueKind.Number && expiresInEl.TryGetInt64(out var secondsNum) && secondsNum > 0)
            {
                expiresAt = now.AddSeconds(secondsNum);
            }
            else if (expiresInEl.ValueKind == JsonValueKind.String
                && long.TryParse(expiresInEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var secondsStr)
                && secondsStr > 0)
            {
                expiresAt = now.AddSeconds(secondsStr);
            }
        }

        return new CachedToken(token, expiresAt);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
}
