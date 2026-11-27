using DigitalBrain.Modules.Configuration;
namespace IntoChat.OS.Modules.Identity.Connections;

public sealed class ConnectionsOptions
{
    public static string SectionName => DigitalBrain.Modules.Configuration.ModuleConfiguration.ConnectionsSectionName;
    // Where providers send browsers back; null means no OAuth login can start.
    public Uri? PublicOrigin { get; set; }
    // Test hosts point a connector at a stub token endpoint; keyed by connector id.
    public Dictionary<string, string> TokenEndpoints { get; set; } = new(StringComparer.Ordinal);
    // Test hosts point a connector at a stub device authorization endpoint; keyed by connector id.
    public Dictionary<string, string> DeviceAuthorizationEndpoints { get; set; } = new(StringComparer.Ordinal);
    public TimeSpan LoginLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public int PendingLoginCapacity { get; set; } = 128;
}
