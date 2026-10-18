namespace DigitalBrain.Authority.Hosted;

public sealed class HostedRuntimeOptions
{
    public const string SectionName = "DigitalBrain:Hosting";
    public string AccountId { get; set; } = "";
    public string BrainId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ControllerCertificateSha256 { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public bool RecoveryOnly { get; set; }

    // Claim types a hosted workspace cookie carries beside IdentityHostOptions' account and brain claims. Live
    // cookies hold these names, so the defaults are the historical values (decision 11).
    public string GrantClaim { get; set; } = "intochat.hosted-grant";
    public string RuntimeClaim { get; set; } = "intochat.runtime";
    public string CloudUserClaim { get; set; } = "intochat.cloud-user";
    public string CloudSessionClaim { get; set; } = "intochat.cloud-session";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(BrainId) && !string.IsNullOrWhiteSpace(RuntimeId) &&
        ControllerCertificateSha256.Length == 64 && ControllerCertificateSha256.All(Uri.IsHexDigit);
}
