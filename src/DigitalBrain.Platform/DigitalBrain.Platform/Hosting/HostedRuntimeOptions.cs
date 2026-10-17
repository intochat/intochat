namespace DigitalBrain.Platform.Hosting;

public sealed class HostedRuntimeOptions
{
    public const string SectionName = "DigitalBrain:Hosting";
    public string AccountId { get; set; } = "";
    public string BrainId { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public string ControllerCertificateSha256 { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public bool RecoveryOnly { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(BrainId) && !string.IsNullOrWhiteSpace(RuntimeId) &&
        ControllerCertificateSha256.Length == 64 && ControllerCertificateSha256.All(Uri.IsHexDigit);
}
