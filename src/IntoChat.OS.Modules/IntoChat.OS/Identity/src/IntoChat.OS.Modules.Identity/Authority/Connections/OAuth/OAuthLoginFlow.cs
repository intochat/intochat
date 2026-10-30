using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Connections.OAuth;

internal sealed class OAuthLoginFlow(OAuthLoginRequests requests, OAuthTokenExchange exchange, IOptions<ConnectionsOptions> options)
{
    public Uri CallbackUri(string connectorId, HttpRequest? request = null)
        => new(Origin(request), $"integrations/{Uri.EscapeDataString(connectorId)}/oauth/callback");

    public Uri LegacyCallbackUri(string connectorId, HttpRequest? request = null)
        => new(Origin(request), $"connections/oauth/{Uri.EscapeDataString(connectorId)}/callback");

    private Uri Origin(HttpRequest? request)
        => options.Value.PublicOrigin
            ?? (request is { Host.HasValue: true } && !string.IsNullOrWhiteSpace(request.Scheme)
                ? new Uri($"{request.Scheme}://{request.Host}")
                : throw new InvalidOperationException("Configure IntoChatOS:Connections:PublicOrigin before starting a browser login."));

    public Task<Uri> StartAsync(string connectionKey, string authMethodId, string? target, CapabilityCaller owner, CancellationToken ct)
        => StartAsync(connectionKey, authMethodId, target, owner, request: null, ct);

    public async Task<Uri> StartAsync(string connectionKey, string authMethodId, string? target, CapabilityCaller owner, HttpRequest? request, CancellationToken ct)
    {
        var (_, connector, _) = ConnectionKeys.Parse(connectionKey);
        var method = exchange.Method(connector, authMethodId);
        var callback = CallbackUri(connector, request);
        var (clientId, _) = await exchange.ClientAsync(method, ct);
        var (state, verifier) = requests.Begin(connectionKey, authMethodId, target, owner);
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
