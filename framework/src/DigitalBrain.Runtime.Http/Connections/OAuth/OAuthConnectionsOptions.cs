using DigitalBrain.Modules.Configuration;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

public sealed class OAuthConnectionsOptions
{
    public const string DefaultProductName = "DigitalBrain";
    public const string DefaultHttpClientName = "DigitalBrain.Connections.OAuth";
    public const string DefaultMeteredHttpClientName = "DigitalBrain.Connections.OAuth.Metered";
    public const string DefaultDeviceHttpClientName = "DigitalBrain.Connections.OAuthDevice";

    public static string SectionName => ModuleConfiguration.ConnectionsSectionName;

    // Where external services send browsers back; null means no OAuth login can start unless inferred from the HTTP request.
    public Uri? PublicOrigin { get; set; }

    // Product display name rendered in browser callback pages.
    public string ProductName { get; set; } = DefaultProductName;

    // Configuration key hint shown when PublicOrigin is missing; defaults to ModuleConfiguration.ConnectionsPublicOriginKey.
    public string? ConfigHint { get; set; }

    // Named HttpClient used for unmetered OAuth token exchange requests.
    public string HttpClientName { get; set; } = DefaultHttpClientName;

    // Named HttpClient used for metered OAuth token exchange requests.
    public string MeteredHttpClientName { get; set; } = DefaultMeteredHttpClientName;

    // Named HttpClient used for RFC 8628 OAuth device authorization requests.
    public string DeviceHttpClientName { get; set; } = DefaultDeviceHttpClientName;

    // Test hosts point a connector at a stub token endpoint; keyed by connector id.
    public Dictionary<string, string> TokenEndpoints { get; set; } = new(StringComparer.Ordinal);

    // Test hosts point a connector at a stub device authorization endpoint; keyed by connector id.
    public Dictionary<string, string> DeviceAuthorizationEndpoints { get; set; } = new(StringComparer.Ordinal);

    public TimeSpan LoginLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public int PendingLoginCapacity { get; set; } = 128;

    internal string ResolvedProductName => string.IsNullOrWhiteSpace(ProductName) ? DefaultProductName : ProductName;

    internal string ResolvedConfigHint => string.IsNullOrWhiteSpace(ConfigHint) ? ModuleConfiguration.ConnectionsPublicOriginKey : ConfigHint;

    internal string ResolvedHttpClientName => string.IsNullOrWhiteSpace(HttpClientName) ? DefaultHttpClientName : HttpClientName;

    internal string ResolvedMeteredHttpClientName => string.IsNullOrWhiteSpace(MeteredHttpClientName) ? DefaultMeteredHttpClientName : MeteredHttpClientName;

    internal string ResolvedDeviceHttpClientName => string.IsNullOrWhiteSpace(DeviceHttpClientName) ? DefaultDeviceHttpClientName : DeviceHttpClientName;
}
