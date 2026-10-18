namespace IntoChat.OS.Framework.Platform.Connections;

public interface IOAuthTokenExchange
{
    Task<OAuthGrant> ExchangeCodeAsync(string connectorId, string authMethodId, string code, string codeVerifier, string redirectUri, string? target, CancellationToken ct);
    Task<OAuthGrant> RefreshAsync(string connectorId, string authMethodId, string refreshToken, CancellationToken ct);
}
