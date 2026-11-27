using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Http.Connections.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Contracts.Connections;

namespace IntoChat.OS.Modules.Identity.Connections.OAuth;

// OAuth 2.0 Device Authorization Grant (RFC 8628): zero-secret public client flow for desktop & CLI.
// Never sends client_secret; never logs or returns device_code or raw tokens.
internal sealed class OAuthDeviceFlow(
    IReadOnlyList<ConnectorDefinition> connectors,
    AuthorityNeurons neurons,
    OAuthDeviceRequests requests,
    IOptions<OAuthConnectionsOptions> options,
    TimeProvider clock,
    IHttpClientFactory clients,
    EdgeAuthority edge,
    IBrainCapabilityPolicy? capabilityPolicy = null,
    ILogger<OAuthDeviceFlow>? logger = null) : IOAuthDeviceFlow
{
    public const string HttpClientName = "IntoChat.OS.Connections.OAuthDevice";
    internal const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const int DefaultExpiresInSeconds = 900;
    private const int MinExpiresInSeconds = 60;
    private const int MaxExpiresInSeconds = 3600;
    private const int DefaultIntervalSeconds = 5;
    private const int MinIntervalSeconds = 1;
    private const int MaxIntervalSeconds = 60;
    private const int SlowDownIncrementSeconds = 5;

    public async Task<OAuthDeviceStartResult> StartAsync(
        BrainId brainId,
        string connectionKey,
        string authMethodId,
        string? target = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(authMethodId);

        var (keyBrain, connectorId, _) = ConnectionKeys.Parse(connectionKey);
        if (!string.Equals(keyBrain, brainId.Value, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The connection must belong to the owning brain.");
        }

        await EnforceExternalConnectorAllowedAsync(brainId.Value, connectorId, ct).ConfigureAwait(false);

        var method = ResolveMethod(connectorId, authMethodId);
        var clientId = await ResolveClientIdAsync(connectorId, method, ct).ConfigureAwait(false);
        var deviceAuthEndpoint = ValidateEndpoint(ResolveDeviceAuthorizationEndpoint(connectorId, method));
        var tokenEndpoint = ValidateEndpoint(ResolveTokenEndpoint(connectorId, method));

        var form = new Dictionary<string, string>(method.AuthorizationParameters, StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
        };
        var scope = string.Join(' ', method.Scopes);
        if (!string.IsNullOrWhiteSpace(scope))
        {
            form["scope"] = scope;
        }

        var (statusCode, body) = await PostFormAsync(deviceAuthEndpoint, form, ct).ConfigureAwait(false);
        if (statusCode is < 200 or >= 300)
        {
            logger?.LogWarning("OAuth device authorization start for {ConnectorId} failed with HTTP {StatusCode}.", connectorId, statusCode);
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }

        using var json = ParseJsonObject(body);
        var root = json.RootElement;

        var deviceCode = ReadTokenField(root, "device_code");
        var userCode = ReadRequiredString(root, "user_code", maxLength: 256);
        var verificationUri = ReadRequiredString(root, "verification_uri", maxLength: 2048);
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var parsedVerifyUri)
            || parsedVerifyUri.Scheme is not ("https" or "http"))
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        string? verificationUriComplete = null;
        if (root.TryGetProperty("verification_uri_complete", out var completeProp) && completeProp.ValueKind == JsonValueKind.String)
        {
            var candidate = completeProp.GetString();
            if (!string.IsNullOrWhiteSpace(candidate)
                && candidate.Length <= 2048
                && Uri.TryCreate(candidate, UriKind.Absolute, out var parsedComplete)
                && parsedComplete.Scheme is "https" or "http")
            {
                verificationUriComplete = candidate;
            }
        }

        var expiresIn = ReadIntInRange(root, "expires_in", DefaultExpiresInSeconds, MinExpiresInSeconds, MaxExpiresInSeconds);
        var interval = ReadIntInRange(root, "interval", DefaultIntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        var expiresAtUtc = clock.GetUtcNow().AddSeconds(expiresIn);

        var flowId = requests.Begin(
            brainId.Value,
            connectionKey,
            authMethodId,
            target,
            deviceCode,
            clientId,
            tokenEndpoint.AbsoluteUri,
            method.Scopes,
            interval,
            expiresAtUtc);

        return new OAuthDeviceStartResult(
            flowId,
            userCode,
            verificationUri,
            verificationUriComplete,
            expiresIn,
            interval);
    }

    public async Task<OAuthDevicePollResult> PollOnceAsync(
        BrainId brainId,
        string flowId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);

        if (!requests.TryGet(flowId, brainId.Value, out var flow, out var expired, out var wrongBrain))
        {
            if (wrongBrain)
            {
                throw new UnauthorizedAccessException("Device authorization flow belongs to another brain.");
            }

            if (expired)
            {
                return new OAuthDevicePollResult(OAuthDevicePollStatus.Expired, 0, null, "The device authorization code has expired. Start again.");
            }

            throw new KeyNotFoundException("Device authorization flow was not found.");
        }

        var (_, connectorId, _) = ConnectionKeys.Parse(flow!.ConnectionKey);
        await EnforceExternalConnectorAllowedAsync(brainId.Value, connectorId, ct).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        if (now < flow.NextAllowedPollUtc)
        {
            return new OAuthDevicePollResult(OAuthDevicePollStatus.Pending, flow.IntervalSeconds, null, null);
        }

        // RFC 8628 §3.4: public client polling sends client_id + device_code + grant_type (zero client_secret).
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = flow.ClientId,
            ["device_code"] = flow.DeviceCode,
            ["grant_type"] = DeviceCodeGrantType,
        };

        var tokenEndpoint = ValidateEndpoint(flow.TokenEndpoint);
        var (statusCode, body) = await PostFormAsync(tokenEndpoint, form, ct).ConfigureAwait(false);

        using var json = ParseJsonObject(body);
        var root = json.RootElement;

        // GitHub returns HTTP 200 with {"error":"authorization_pending"}, while RFC 8628 §3.5 uses HTTP 400.
        // Inspect the error field regardless of 2xx vs 4xx.
        if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
        {
            var errorCode = errorProp.GetString();
            switch (errorCode)
            {
                case "authorization_pending":
                    requests.UpdatePollSchedule(flowId, brainId.Value, flow.IntervalSeconds, clock.GetUtcNow().AddSeconds(flow.IntervalSeconds));
                    return new OAuthDevicePollResult(OAuthDevicePollStatus.Pending, flow.IntervalSeconds, null, null);

                case "slow_down":
                    // RFC 8628 §3.5: increase minimum polling interval by 5 seconds.
                    var bumped = Math.Min(flow.IntervalSeconds + SlowDownIncrementSeconds, MaxIntervalSeconds);
                    requests.UpdatePollSchedule(flowId, brainId.Value, bumped, clock.GetUtcNow().AddSeconds(bumped));
                    return new OAuthDevicePollResult(OAuthDevicePollStatus.Pending, bumped, null, null);

                case "expired_token":
                    requests.Remove(flowId, brainId.Value);
                    return new OAuthDevicePollResult(OAuthDevicePollStatus.Expired, 0, null, "The device authorization code has expired. Start again.");

                case "access_denied":
                    requests.Remove(flowId, brainId.Value);
                    return new OAuthDevicePollResult(OAuthDevicePollStatus.Denied, 0, null, "Authorization was denied.");

                default:
                    requests.Remove(flowId, brainId.Value);
                    logger?.LogWarning("OAuth device poll for {ConnectorId} returned error code '{ErrorCode}'.", connectorId, errorCode);
                    throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
            }
        }

        if (statusCode is < 200 or >= 300)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }

        var accessToken = ReadTokenField(root, "access_token");
        string? refreshToken = null;
        if (root.TryGetProperty("refresh_token", out var refreshProp) && refreshProp.ValueKind == JsonValueKind.String)
        {
            refreshToken = refreshProp.GetString();
            if (refreshToken is not null && (refreshToken.Length > 16384 || refreshToken.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
            {
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }
        }

        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresProp)
            && double.TryParse(expiresProp.ToString(), CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 0 and < 315360000
            ? clock.GetUtcNow().AddSeconds(seconds)
            : null;

        var scopes = string.Join(' ', flow.Scopes);
        if (root.TryGetProperty("scope", out var scopeProp))
        {
            if (scopeProp.ValueKind != JsonValueKind.String
                || scopeProp.GetString() is not { Length: <= 16384 } returnedScopes
                || returnedScopes.Any(char.IsControl))
            {
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }

            scopes = returnedScopes;
        }

        // Atomically consume the flow so each authorized device flow is strictly single-use.
        if (!requests.TryConsume(flowId, brainId.Value, out _))
        {
            return new OAuthDevicePollResult(OAuthDevicePollStatus.Expired, 0, null, "The device authorization flow has already been completed or expired.");
        }

        var grant = new OAuthGrant(flow.AuthMethodId, accessToken, refreshToken, expiresAt, scopes, flow.Target, body);
        var view = await neurons.ConnectionInbound(flow.ConnectionKey).AcceptGrant(grant).ConfigureAwait(false);
        return new OAuthDevicePollResult(OAuthDevicePollStatus.Connected, flow.IntervalSeconds, view, null);
    }

    internal static Uri ValidateEndpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("An OAuth device endpoint must be a valid absolute HTTPS URL.", nameof(url));
        }

        if (uri.Scheme == "https")
        {
            if (IPAddress.TryParse(uri.DnsSafeHost, out var httpsIp) && IsForbiddenAddress(httpsIp))
            {
                throw new ArgumentException("An OAuth device endpoint may not target a private or link-local IP address.", nameof(url));
            }

            return uri;
        }

        // Allow loopback HTTP only for local test stubs.
        if (uri.Scheme == "http" && IsLoopbackHost(uri.DnsSafeHost))
        {
            return uri;
        }

        throw new ArgumentException("An OAuth device endpoint must use HTTPS.", nameof(url));
    }

    private async Task EnforceExternalConnectorAllowedAsync(string brainId, string connectorId, CancellationToken ct)
    {
        if (capabilityPolicy is null)
        {
            return;
        }

        var decision = await capabilityPolicy.EvaluateAsync(brainId, BrainCapability.ExternalConnector, connectorId, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new ClaimRequiredException(decision);
        }
    }

    private OAuthDeviceMethod ResolveMethod(string connectorId, string authMethodId)
        => connectors.FirstOrDefault(c => string.Equals(c.Id, connectorId, StringComparison.Ordinal))
               ?.DeviceMethods.FirstOrDefault(m => string.Equals(m.Id, authMethodId, StringComparison.Ordinal))
           ?? throw new ArgumentException($"Connector '{connectorId}' has no OAuth device method '{authMethodId}'.", nameof(authMethodId));

    private string ResolveDeviceAuthorizationEndpoint(string connectorId, OAuthDeviceMethod method)
        => options.Value.DeviceAuthorizationEndpoints.TryGetValue(connectorId, out var endpoint) && !string.IsNullOrWhiteSpace(endpoint)
            ? endpoint
            : method.DeviceAuthorizationEndpoint;

    private string ResolveTokenEndpoint(string connectorId, OAuthDeviceMethod method)
        => options.Value.TokenEndpoints.TryGetValue(connectorId, out var endpoint) && !string.IsNullOrWhiteSpace(endpoint)
            ? endpoint
            : method.TokenEndpoint;

    private async Task<string> ResolveClientIdAsync(string connectorId, OAuthDeviceMethod method, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(method.IntegrationId))
        {
            var integration = neurons.Integration(method.IntegrationId);
            if ((await integration.Read().WaitAsync(ct).ConfigureAwait(false)).Status == IntegrationStatus.Ready)
            {
                var released = await edge.AsKernel(BrainId.Kernel, () => integration.Release().WaitAsync(ct)).ConfigureAwait(false);
                if (released.Values.TryGetValue(method.ClientIdField, out var overrideClientId)
                    && !string.IsNullOrWhiteSpace(overrideClientId))
                {
                    return overrideClientId.Trim();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(method.DefaultClientId))
        {
            return method.DefaultClientId.Trim();
        }

        throw new InvalidOperationException($"OAuth device method '{method.Id}' on connector '{connectorId}' has no client_id configured.");
    }

    private async Task<(int StatusCode, string Body)> PostFormAsync(Uri endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var body = await ReadBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }
        catch (HttpRequestException)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }
        catch (IOException)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }
    }

    private static JsonDocument ParseJsonObject(string body)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }

            return document;
        }
        catch (JsonException)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }
    }

    private static string ReadTokenField(JsonElement root, string property)
    {
        var value = root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement root, string property, int maxLength)
    {
        var value = root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        return value;
    }

    private static int ReadIntInRange(JsonElement root, string property, int fallback, int min, int max)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStr))
        {
            return Math.Clamp(parsedStr, min, max);
        }

        return fallback;
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        const int maximum = 256 * 1024;
        if (content.Headers.ContentLength > maximum)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) { break; }
            if (output.Length + count > maximum)
            {
                throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
            }

            output.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static bool IsForbiddenAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
        if (IPAddress.IsLoopback(address)) { return false; }
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) { return true; }
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
