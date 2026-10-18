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

    public bool Validate() => !string.IsNullOrWhiteSpace(CookieName)
        && !string.IsNullOrWhiteSpace(ProtectionApplicationName)
        && !string.IsNullOrWhiteSpace(ProtectionContainerName);
}
