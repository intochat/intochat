namespace IntoChat.OS.Authority.Identity.Configuration;

// The deployment's HTTP identity. The platform binds it in the silo; the AppHost projects the
// same keys to every resource that serves or shares the session, so silo and clients agree.
public sealed class IdentityHostOptions
{
    public const string SectionName = "IntoChatOS:Identity";

    public static string Key(string property) => SectionName + ":" + property;

    public string CookieName { get; set; } = "os.session";
    public string ProtectionApplicationName { get; set; } = "os.v1";
    public string ProtectionContainerName { get; set; } = "os-protection-v1";

    // Claim types the session cookie carries. The defaults are brand-neutral; a distribution whose live cookies
    // hold other names pins them by configuration (renaming them signs every session out).
    public string AccountClaim { get; set; } = "os.account";
    public string BrainClaim { get; set; } = "os.brain";

    // The identity directory grain's key. Every deployment's accounts, principals and passwords are stored under it,
    // so a distribution with existing accounts pins its historical value by configuration; it cannot change without a migration.
    public string DirectoryKey { get; set; } = "identity-directory";

    public bool Validate() => !string.IsNullOrWhiteSpace(CookieName)
        && !string.IsNullOrWhiteSpace(ProtectionApplicationName)
        && !string.IsNullOrWhiteSpace(ProtectionContainerName)
        && !string.IsNullOrWhiteSpace(AccountClaim)
        && !string.IsNullOrWhiteSpace(BrainClaim)
        && !string.IsNullOrWhiteSpace(DirectoryKey);
}
