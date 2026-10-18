namespace DigitalBrain.Authority.Identity.Configuration;

// The deployment's HTTP identity. The platform binds it in the silo; the AppHost projects the
// same keys to every resource that serves or shares the session, so silo and clients agree.
public sealed class IdentityHostOptions
{
    public const string SectionName = "DigitalBrain:Identity";

    public static string Key(string property) => SectionName + ":" + property;

    public string CookieName { get; set; } = "digitalbrain.session";
    public string ProtectionApplicationName { get; set; } = "DigitalBrain.v1";
    public string ProtectionContainerName { get; set; } = "digitalbrain-protection-v1";

    // Claim types the session cookie carries. Live cookies hold these names, so the defaults are the historical
    // values and stay as they are (decision 11); renaming them signs every session out.
    public string AccountClaim { get; set; } = "intochat.account";
    public string BrainClaim { get; set; } = "intochat.brain";

    public bool Validate() => !string.IsNullOrWhiteSpace(CookieName)
        && !string.IsNullOrWhiteSpace(ProtectionApplicationName)
        && !string.IsNullOrWhiteSpace(ProtectionContainerName)
        && !string.IsNullOrWhiteSpace(AccountClaim)
        && !string.IsNullOrWhiteSpace(BrainClaim);
}
