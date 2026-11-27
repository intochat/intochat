namespace DigitalBrain.Capabilities.Connections.Connectors;

/// <summary>
/// An OAuth 2.0 Device Authorization Grant (RFC 8628) authentication method for a connector.
/// Uses a public client identifier (no client secret): either a built-in <see cref="DefaultClientId"/>
/// or an optional host integration override named by <see cref="IntegrationId"/>.
/// </summary>
public sealed record OAuthDeviceMethod(
    string Id,
    string DeviceAuthorizationEndpoint,
    string TokenEndpoint,
    string[] Scopes,
    string? DefaultClientId = null,
    string? IntegrationId = null,
    string ClientIdField = "ClientId",
    string? DisplayName = null)
{
    /// <summary>Extra form parameters sent to the device authorization endpoint.</summary>
    public Dictionary<string, string> AuthorizationParameters { get; init; } = new(StringComparer.Ordinal);
}
