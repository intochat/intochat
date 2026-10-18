namespace IntoChat.OS.Authority.Hosted;

public sealed class HostedRuntimeOptions
{
    public const string SectionName = IntoChat.OS.Framework.IntoChatOSNames.HostingConfigurationKey;
    public string AccountId { get; set; } = "";
    public string BrainId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ControllerCertificateSha256 { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public bool RecoveryOnly { get; set; }

    // Claim types a hosted workspace cookie carries beside IdentityHostOptions' account and brain claims. Live
    // cookies hold these names; the defaults are brand-neutral and a distribution pins its historical values by configuration.
    public string GrantClaim { get; set; } = "os.hosted-grant";
    public string RuntimeClaim { get; set; } = "os.runtime";
    public string CloudUserClaim { get; set; } = "os.cloud-user";
    public string CloudSessionClaim { get; set; } = "os.cloud-session";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(BrainId) && !string.IsNullOrWhiteSpace(RuntimeId) &&
        ControllerCertificateSha256.Length == 64 && ControllerCertificateSha256.All(Uri.IsHexDigit);
}
