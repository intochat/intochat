using DigitalBrain;
using DigitalBrain.Access;
using System.Globalization;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Connections.OAuth;

internal sealed class OAuthTokenExchange(
    IReadOnlyList<ConnectorDefinition> connectors, AuthorityNeurons neurons, IOptions<ConnectionsOptions> options,
    TimeProvider clock, IHttpClientFactory clients, ModulePriceListRegistry operations, EdgeAuthority edge) : IOAuthTokenExchange
{
    public const string HttpClientName = "IntoChat.OS.Connections.OAuth";
    public const string MeteredHttpClientName = "IntoChat.OS.Connections.OAuth.Metered";

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

    // The Integration neuron releases only to the kernel; the client values never leave this method.
    internal async Task<(string Id, string Secret)> ClientAsync(OAuthMethod method, CancellationToken ct)
    {
        var integration = neurons.Integration(method.IntegrationId);
        if ((await integration.Read().WaitAsync(ct)).Status != IntegrationStatus.Ready)
        { throw new InvalidOperationException($"Integration '{method.IntegrationId}' is not configured."); }
        var released = await edge.AsKernel(BrainId.Kernel, () => integration.Release().WaitAsync(ct));
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
        var (id, secret) = await ClientAsync(method, ct);
        form["client_id"] = id;
        form["client_secret"] = secret;
        using var client = clients.CreateClient(operation is null ? HttpClientName : MeteredHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint(connectorId, method)) { Content = new FormUrlEncodedContent(form) };
        if (operation is not null)
        { request.SetExternalCall(new(operation, Funding.OwnConnection, new(Neuron: connectionKey), Guid.NewGuid().ToString("N"))); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var body = await ReadBody(response.Content, linked.Token);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
            if (!response.IsSuccessStatusCode)
            {
                var rejected = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "invalid_grant";
                throw new OAuthTokenExchangeException(rejected ? OAuthTokenFailure.ReconsentRequired : OAuthTokenFailure.Unavailable);
            }
            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(access) || access.Length > 16384 || access.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (refresh is not null && (refresh.Length > 16384 || refresh.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
            { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
            DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var e) && double.TryParse(e.ToString(), CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 315360000
                ? clock.GetUtcNow().AddSeconds(seconds) : null;
            var scopes = string.Join(' ', method.Scopes);
            if (root.TryGetProperty("scope", out var s))
            {
                if (s.ValueKind != JsonValueKind.String || s.GetString() is not { Length: <= 16384 } returnedScopes
                    || returnedScopes.Any(char.IsControl))
                { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
                scopes = returnedScopes;
            }
            return new OAuthGrant(authMethodId, access, refresh, expiresAt, scopes, target, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable); }
        catch (HttpRequestException) { throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable); }
        catch (IOException) { throw new OAuthTokenExchangeException(OAuthTokenFailure.Unavailable); }
        catch (JsonException) { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
    }

    private static async Task<string> ReadBody(HttpContent content, CancellationToken ct)
    {
        const int maximum = 256 * 1024;
        if (content.Headers.ContentLength > maximum) { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) { break; }
            if (output.Length + count > maximum) { throw new OAuthTokenExchangeException(OAuthTokenFailure.InvalidResponse); }
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
