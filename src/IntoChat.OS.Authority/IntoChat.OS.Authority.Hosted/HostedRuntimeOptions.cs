namespace IntoChat.OS.Authority.Hosted;

public sealed class HostedRuntimeOptions
{
    public const string SectionName = IntoChat.OS.Framework.IntoChatOSNames.HostingConfigurationKey;
    // The Hosting section configures a hosted allocation only when it carries more than the run-mode marker.
    public static bool IsHostedAllocation(Microsoft.Extensions.Configuration.IConfiguration configuration)
        => configuration.GetSection(SectionName).GetChildren()
            .Any(child => !child.Key.Equals("Mode", StringComparison.OrdinalIgnoreCase));

    public string AccountId { get; set; } = "";
    public string BrainId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ControllerCertificateSha256 { get; set; } = "";
    public bool RecoveryOnly { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(BrainId) && !string.IsNullOrWhiteSpace(RuntimeId) &&
        ControllerCertificateSha256.Length == 64 && ControllerCertificateSha256.All(Uri.IsHexDigit);
}
