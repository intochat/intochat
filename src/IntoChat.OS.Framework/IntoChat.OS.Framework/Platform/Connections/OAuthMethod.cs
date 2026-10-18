namespace IntoChat.OS.Framework.Platform.Connections;

// One browser-authorized method of a connector. The client id and secret live in the named
// Integration; the Platform owns the redirect, the callback, the code exchange and refresh.
public sealed record OAuthMethod(
    string Id,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string[] Scopes,
    string IntegrationId,
    string ClientIdField = "ClientId",
    string ClientSecretField = "ClientSecret",
    bool Pkce = true)
{
    // Extra query parameters on the authorization URL (Google's access_type=offline).
    public Dictionary<string, string> AuthorizationParameters { get; init; } = new(StringComparer.Ordinal);
}
