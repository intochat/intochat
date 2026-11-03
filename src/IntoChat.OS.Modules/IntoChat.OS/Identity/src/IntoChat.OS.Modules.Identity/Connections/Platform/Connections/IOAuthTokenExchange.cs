using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

public interface IOAuthTokenExchange
{
    Task<OAuthGrant> ExchangeCodeAsync(string connectionKey, string authMethodId, string code, string codeVerifier, string redirectUri, string? target, CancellationToken ct);
    Task<OAuthGrant> RefreshAsync(string connectionKey, string authMethodId, string refreshToken, CancellationToken ct);
}
