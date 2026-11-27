using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Neurons;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

/// <summary>Delivers a completed OAuth device grant to the target connection.</summary>
public interface IOAuthDeviceGrantAcceptor
{
    Task<ConnectionView> AcceptGrantAsync(string connectionKey, OAuthGrant grant, CancellationToken ct = default);
}

/// <summary>Optional policy gate invoked before starting or polling an OAuth device flow.</summary>
public interface IOAuthDeviceAuthorizationGate
{
    ValueTask AuthorizeAsync(BrainId brainId, string connectorId, CancellationToken ct = default);
}

internal sealed class NeuronProxiesOAuthDeviceGrantAcceptor(INeuronProxies proxies) : IOAuthDeviceGrantAcceptor
{
    public Task<ConnectionView> AcceptGrantAsync(string connectionKey, OAuthGrant grant, CancellationToken ct = default)
        => proxies.Get<IConnectionInbound>(ConnectionKeys.IdFor(connectionKey)).AcceptGrant(grant);
}

// OAuth 2.0 Device Authorization Grant (RFC 8628): zero-secret public client flow for desktop & CLI.
// Never sends client_secret; never logs or returns device_code or raw tokens.
public sealed partial class OAuthDeviceFlow(
    IReadOnlyList<ConnectorDefinition> connectors,
    IOAuthIntegrationResolver integrations,
    OAuthDeviceRequests requests,
    IOptions<OAuthConnectionsOptions> options,
    TimeProvider clock,
    IHttpClientFactory clients,
    IOAuthDeviceGrantAcceptor grantAcceptor,
    IOAuthDeviceAuthorizationGate? authorizationGate = null,
    ILogger<OAuthDeviceFlow>? logger = null) : IOAuthDeviceFlow
{
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const int DefaultExpiresInSeconds = 900;
    private const int MinExpiresInSeconds = 60;
    private const int MaxExpiresInSeconds = 3600;
    private const int DefaultIntervalSeconds = 5;
    private const int MinIntervalSeconds = 1;
    private const int MaxIntervalSeconds = 60;
    private const int SlowDownIncrementSeconds = 5;

    private static readonly Action<HttpRequestMessage> AcceptJson =
        static request => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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

        if (authorizationGate is not null)
        {
            await authorizationGate.AuthorizeAsync(brainId, connectorId, ct).ConfigureAwait(false);
        }

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

        var response = await OAuthHttpProtocol.PostFormAsync(
            clients,
            options.Value.ResolvedDeviceHttpClientName,
            deviceAuthEndpoint,
            form,
            clock,
            AcceptJson,
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (logger is not null) { LogStartFailed(logger, connectorId, (int)response.StatusCode); }
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }

        using var json = OAuthHttpProtocol.ParseJsonObject(response.Body);
        var root = json.RootElement;

        var deviceCode = OAuthHttpProtocol.ReadTokenField(root, "device_code");
        var userCode = ReadRequiredString(root, "user_code", maxLength: 256);
        var verificationUri = ReadRequiredString(root, "verification_uri", maxLength: 2048);
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var parsedVerifyUri)
            || !IsAllowedVerificationUri(parsedVerifyUri))
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
        }

        string? verificationUriComplete = null;
        if (root.TryGetProperty("verification_uri_complete", out var completeProp) && completeProp.ValueKind == JsonValueKind.String)
        {
            var candidate = completeProp.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (candidate.Length > 2048
                    || candidate.Any(char.IsControl)
                    || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsedComplete)
                    || !IsAllowedVerificationUri(parsedComplete))
                {
                    throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse);
                }

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

    public Task<OAuthDevicePollResult> PollOnceAsync(
        BrainId brainId,
        string flowId,
        CancellationToken ct = default)
        => PollOnceAsync(brainId, flowId, expectedConnectionId: null, ct);

    public async Task<OAuthDevicePollResult> PollOnceAsync(
        BrainId brainId,
        string flowId,
        string? expectedConnectionId,
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

        if (!string.IsNullOrWhiteSpace(expectedConnectionId)
            && !MatchesConnectionId(flow!.ConnectionKey, brainId.Value, expectedConnectionId))
        {
            throw new KeyNotFoundException("Device authorization flow does not match the specified connection.");
        }

        var (_, connectorId, _) = ConnectionKeys.Parse(flow!.ConnectionKey);
        if (authorizationGate is not null)
        {
            await authorizationGate.AuthorizeAsync(brainId, connectorId, ct).ConfigureAwait(false);
        }

        // Atomically check throttle/expiry and advance NextAllowedPollUtc before issuing the upstream HTTP poll,
        // so concurrent /device/poll calls cannot burst the authorization server.
        if (!requests.TryReservePoll(flowId, brainId.Value, out var reserved, out var expiredAfterCheck))
        {
            if (expiredAfterCheck)
            {
                return new OAuthDevicePollResult(OAuthDevicePollStatus.Expired, 0, null, "The device authorization code has expired. Start again.");
            }

            if (reserved is null)
            {
                throw new KeyNotFoundException("Device authorization flow was not found.");
            }

            return new OAuthDevicePollResult(OAuthDevicePollStatus.Pending, reserved.IntervalSeconds, null, null);
        }

        flow = reserved!;

        // RFC 8628 §3.4: public client polling sends client_id + device_code + grant_type (zero client_secret).
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = flow.ClientId,
            ["device_code"] = flow.DeviceCode,
            ["grant_type"] = DeviceCodeGrantType,
        };

        var tokenEndpoint = ValidateEndpoint(flow.TokenEndpoint);
        var response = await OAuthHttpProtocol.PostFormAsync(
            clients,
            options.Value.ResolvedDeviceHttpClientName,
            tokenEndpoint,
            form,
            clock,
            AcceptJson,
            ct).ConfigureAwait(false);

        using var json = OAuthHttpProtocol.ParseJsonObject(response.Body);
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
                    if (logger is not null) { LogPollError(logger, connectorId, errorCode); }
                    throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable);
        }

        var grant = OAuthHttpProtocol.ParseTokenGrant(root, response.Body, flow.AuthMethodId, flow.Scopes, flow.Target, clock);

        // Atomically consume the flow so each authorized device flow is strictly single-use.
        if (!requests.TryConsume(flowId, brainId.Value, out _))
        {
            return new OAuthDevicePollResult(OAuthDevicePollStatus.Expired, 0, null, "The device authorization flow has already been completed or expired.");
        }

        var view = await grantAcceptor.AcceptGrantAsync(flow.ConnectionKey, grant, ct).ConfigureAwait(false);
        return new OAuthDevicePollResult(OAuthDevicePollStatus.Connected, flow.IntervalSeconds, view, null);
    }

    public static Uri ValidateEndpoint(string url)
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
            if (IPAddress.TryParse(uri.DnsSafeHost, out var httpsIp) && !HttpHosts.IsPublicOrLoopback(httpsIp))
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

    public static bool IsAllowedVerificationUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (uri.Scheme == "https")
        {
            return !IPAddress.TryParse(uri.DnsSafeHost, out var httpsIp) || HttpHosts.IsPublicOrLoopback(httpsIp);
        }

        return uri.Scheme == "http" && IsLoopbackHost(uri.DnsSafeHost);
    }

    public static bool MatchesConnectionId(string flowConnectionKey, string brainId, string expectedConnectionId)
    {
        var (_, flowConnectorId, flowConnectionId) = ConnectionKeys.Parse(flowConnectionKey);
        var unescaped = Uri.UnescapeDataString(expectedConnectionId);
        if (ConnectionKeys.TryParse(unescaped, out var expectedKey))
        {
            return string.Equals(expectedKey.BrainId, brainId, StringComparison.Ordinal)
                && string.Equals(expectedKey.ConnectorId, flowConnectorId, StringComparison.Ordinal)
                && string.Equals(expectedKey.ConnectionId, flowConnectionId, StringComparison.Ordinal);
        }

        return string.Equals(unescaped, flowConnectionId, StringComparison.Ordinal)
            || string.Equals(expectedConnectionId, flowConnectionId, StringComparison.Ordinal);
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
            var integration = integrations.Get(method.IntegrationId);
            var snapshot = await integration.ReadAsync(ct).ConfigureAwait(false);
            // Public ClientId settings are readable even when confidential secrets (e.g. PrivateKeyPem) are unconfigured.
            if (snapshot.Settings.TryGetValue(method.ClientIdField, out var settingClientId)
                && !string.IsNullOrWhiteSpace(settingClientId))
            {
                return settingClientId.Trim();
            }

            if (snapshot.Status == IntegrationStatus.Ready)
            {
                var released = await integration.ReleaseAsync(ct).ConfigureAwait(false);
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

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    [LoggerMessage(Level = LogLevel.Warning, Message = "OAuth device authorization start for {ConnectorId} failed with HTTP {StatusCode}.")]
    private static partial void LogStartFailed(ILogger logger, string connectorId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OAuth device poll for {ConnectorId} returned error code '{ErrorCode}'.")]
    private static partial void LogPollError(ILogger logger, string connectorId, string? errorCode);
}
