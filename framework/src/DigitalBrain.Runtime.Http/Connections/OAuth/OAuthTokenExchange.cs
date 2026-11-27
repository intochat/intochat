using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

public sealed class OAuthTokenExchange(
    IReadOnlyList<ConnectorDefinition> connectors,
    IOAuthIntegrationResolver integrations,
    IOptions<OAuthConnectionsOptions> options,
    TimeProvider clock,
    IHttpClientFactory clients,
    ModulePriceListRegistry operations,
    EdgeAuthority edge) : IOAuthTokenExchange
{
    public Task<OAuthGrant> ExchangeCodeAsync(string connectionKey, string authMethodId, string code, string codeVerifier, string redirectUri, string? target, CancellationToken ct)
        => PostAsync(connectionKey, authMethodId, target, false, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
        }, ct);

    public Task<OAuthGrant> RefreshAsync(string connectionKey, string authMethodId, string refreshToken, CancellationToken ct)
        => PostAsync(connectionKey, authMethodId, null, true, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    internal OAuthMethod Method(string connectorId, string authMethodId)
        => connectors.FirstOrDefault(c => c.Id == connectorId)?.OAuthMethods.FirstOrDefault(m => m.Id == authMethodId)
            ?? throw new ArgumentException($"Connector '{connectorId}' has no OAuth method '{authMethodId}'.");

    internal string TokenEndpoint(string connectorId, OAuthMethod method)
        => options.Value.TokenEndpoints.TryGetValue(connectorId, out var endpoint) ? endpoint : method.TokenEndpoint;

    // The integration handle releases only under kernel scope; the client values never leave this method.
    internal async Task<(string Id, string Secret)> ClientAsync(OAuthMethod method, CancellationToken ct)
    {
        var integration = integrations.Get(method.IntegrationId);
        if ((await integration.ReadAsync(ct).ConfigureAwait(false)).Status != IntegrationStatus.Ready)
        { throw new InvalidOperationException($"Integration '{method.IntegrationId}' is not configured."); }
        var released = await integration.ReleaseAsync(ct).ConfigureAwait(false);
        // An optional client field can be missing from a Ready integration; refuse before any redirect.
        if (!released.Values.TryGetValue(method.ClientIdField, out var id) || string.IsNullOrWhiteSpace(id)
            || !released.Values.TryGetValue(method.ClientSecretField, out var secret) || string.IsNullOrWhiteSpace(secret))
        { throw new InvalidOperationException($"Integration '{method.IntegrationId}' has no OAuth client configured."); }
        return (id, secret);
    }

    private async Task<OAuthGrant> PostAsync(string connectionKey, string authMethodId, string? target, bool refreshGrant, Dictionary<string, string> form, CancellationToken ct)
    {
        var (brain, connectorId, _) = ConnectionKeys.Parse(connectionKey);
        var caller = edge.Current ?? throw new UnauthorizedAccessException("The token exchange runs only for a stamped caller.");
        if (brain != caller.Brain.Value)
        { throw new UnauthorizedAccessException("The connection must belong to the current brain."); }
        var method = Method(connectorId, authMethodId);
        var operation = refreshGrant ? method.ExternalOperations?.Refresh : method.ExternalOperations?.CodeExchange;
        if (operation is not null)
        {
            var trace = TraceContext.Current;
            if (trace is null || trace.BrainId != brain || trace.PayerAccountId != (caller.For?.Account ?? caller.Brain.Value))
            { throw new UnauthorizedAccessException("The token exchange requires the current owner's compute trace."); }
            if (operations.GetOperation(operation).Integration != method.IntegrationId)
            { throw new InvalidOperationException("The OAuth operation must belong to its declared integration."); }
        }
        var (id, secret) = await ClientAsync(method, ct).ConfigureAwait(false);
        form["client_id"] = id;
        form["client_secret"] = secret;
        var clientName = operation is null ? options.Value.ResolvedHttpClientName : options.Value.ResolvedMeteredHttpClientName;
        var response = await OAuthHttpProtocol.PostFormAsync(
            clients,
            clientName,
            new Uri(TokenEndpoint(connectorId, method)),
            form,
            clock,
            operation is not null
                ? request => request.SetExternalCall(new(operation, Funding.OwnConnection, new(Neuron: connectionKey), Guid.NewGuid().ToString("N")))
                : null,
            ct).ConfigureAwait(false);
        using var json = OAuthHttpProtocol.ParseJsonObject(response.Body);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var rejected = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "invalid_grant";
            throw new OAuthTokenExchangeException(rejected ? OAuthTokenFailure.ReconsentRequired : OAuthTokenFailure.Unavailable);
        }
        return OAuthHttpProtocol.ParseTokenGrant(root, response.Body, authMethodId, method.Scopes, target, clock);
    }
}
