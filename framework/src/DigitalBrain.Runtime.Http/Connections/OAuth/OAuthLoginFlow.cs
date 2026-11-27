using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

public sealed class OAuthLoginFlow(
    OAuthLoginRequests requests,
    OAuthTokenExchange exchange,
    IOptions<OAuthConnectionsOptions> options,
    ICallerAmbient? callers = null)
{
    public Uri CallbackUri(string connectorId, HttpRequest? request = null)
        => new(Origin(request), $"integrations/{Uri.EscapeDataString(connectorId)}/oauth/callback");

    private Uri Origin(HttpRequest? request)
        => options.Value.PublicOrigin
            ?? (request is { Host.HasValue: true } && !string.IsNullOrWhiteSpace(request.Scheme)
                ? new Uri($"{request.Scheme}://{request.Host}")
                : throw new InvalidOperationException($"Configure {options.Value.ResolvedConfigHint} before starting a browser login."));

    public Task<Uri> StartAsync(string connectionKey, string authMethodId, string? target, CancellationToken ct)
        => StartAsync(connectionKey, authMethodId, target, request: null, ct);

    // The login belongs to the person the stamped caller acts for, in the brain it acts in.
    public async Task<Uri> StartAsync(string connectionKey, string authMethodId, string? target, HttpRequest? request, CancellationToken ct)
    {
        var caller = callers?.Current ?? throw new UnauthorizedAccessException("A browser login starts only for a stamped caller.");
        var owner = CallerRules.ActsFor(caller) ?? throw new UnauthorizedAccessException("A browser login starts only for a person.");
        var (_, connector, _) = ConnectionKeys.Parse(connectionKey);
        var method = exchange.Method(connector, authMethodId);
        var callback = CallbackUri(connector, request);
        var (clientId, _) = await exchange.ClientAsync(method, ct).ConfigureAwait(false);
        var (state, verifier) = requests.Begin(connectionKey, authMethodId, target, owner, caller.Brain.Value);
        var query = new Dictionary<string, string>(method.AuthorizationParameters, StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = callback.AbsoluteUri,
            ["scope"] = string.Join(' ', method.Scopes),
            ["state"] = state,
        };
        if (method.Pkce)
        {
            query["code_challenge"] = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            query["code_challenge_method"] = "S256";
        }
        return new Uri(method.AuthorizationEndpoint + "?" + string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }
}
