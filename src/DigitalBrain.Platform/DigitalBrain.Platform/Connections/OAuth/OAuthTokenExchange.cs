using System.Globalization;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform.Integrations;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Connections.OAuth;

internal sealed class OAuthTokenExchange(
    IReadOnlyList<ConnectorDefinition> connectors, IGrainFactory grains, IOptions<ConnectionsOptions> options,
    TimeProvider clock, IHttpClientFactory clients) : IOAuthTokenExchange
{
    public const string HttpClientName = "DigitalBrain.Connections.OAuth";

    public Task<OAuthGrant> ExchangeCodeAsync(string connectorId, string authMethodId, string code, string codeVerifier, string redirectUri, string? target, CancellationToken ct)
        => PostAsync(connectorId, authMethodId, target, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        }, ct);

    public Task<OAuthGrant> RefreshAsync(string connectorId, string authMethodId, string refreshToken, CancellationToken ct)
        => PostAsync(connectorId, authMethodId, null, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    internal OAuthMethod Method(string connectorId, string authMethodId)
        => connectors.FirstOrDefault(c => c.Id == connectorId)?.OAuthMethods.FirstOrDefault(m => m.Id == authMethodId)
            ?? throw new ArgumentException($"Connector '{connectorId}' has no OAuth method '{authMethodId}'.");

    internal string TokenEndpoint(string connectorId, OAuthMethod method)
        => options.Value.TokenEndpoints.TryGetValue(connectorId, out var endpoint) ? endpoint : method.TokenEndpoint;

    // The Integration neuron's Release demands a platform stamp: this is one of the two places the
    // Platform ring manufactures one, and it never leaves this method.
    internal async Task<(string Id, string Secret)> ClientAsync(OAuthMethod method, CancellationToken ct)
    {
        var integration = grains.GetGrain<IIntegration>(IntegrationVault.GrainKeyPrefix + method.IntegrationId);
        if ((await integration.Read().WaitAsync(ct)).Status != IntegrationStatus.Ready)
        { throw new InvalidOperationException($"Integration '{method.IntegrationId}' is not configured."); }
        var released = await integration.Release(new CallerContext
        {
            PrincipalId = IntegrationVault.CallerAppId, AccountId = IntegrationVault.Owner, BrainId = IntegrationVault.Owner,
            Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = IntegrationVault.CallerAppId,
        }).WaitAsync(ct);
        return (released.Values[method.ClientIdField], released.Values[method.ClientSecretField]);
    }

    private async Task<OAuthGrant> PostAsync(string connectorId, string authMethodId, string? target, Dictionary<string, string> form, CancellationToken ct)
    {
        var method = Method(connectorId, authMethodId);
        var (id, secret) = await ClientAsync(method, ct);
        form["client_id"] = id;
        form["client_secret"] = secret;
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.PostAsync(TokenEndpoint(connectorId, method), new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        { throw new InvalidOperationException($"The {connectorId} token endpoint answered HTTP {(int)response.StatusCode}."); }
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(access) || access.Length > 16384 || access.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        { throw new InvalidOperationException($"The {connectorId} token endpoint returned an invalid token."); }
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var e) && double.TryParse(e.ToString(), CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? clock.GetUtcNow().AddSeconds(seconds) : null;
        var scopes = root.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : string.Join(' ', method.Scopes);
        return new OAuthGrant(authMethodId, access, refresh, expiresAt, scopes, target, body);
    }
}
