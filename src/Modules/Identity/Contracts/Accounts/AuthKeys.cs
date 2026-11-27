namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

// Where the identity module reads its sign-in configuration: the keys a launcher or a deployment writes. The values,
// the bootstrap password above all, are the identity module's own options (AuthOptions); this names only where they go.
public static class AuthKeys
{
    public const string SectionName = "IntoChatOS:Auth";
    public const string Posture = SectionName + ":Posture";
    public const string Username = SectionName + ":Username";
    public const string Password = SectionName + ":Password";
    public const string AllowOpenOnNetwork = SectionName + ":AllowOpenOnNetwork";
    public const string TrustedIssuers = SectionName + ":TrustedIssuers";
    public const string PrincipalLinks = SectionName + ":PrincipalLinks";
    public const string ExternalReturnOrigins = SectionName + ":ExternalReturnOrigins";
    public const string Day0EphemeralTry = SectionName + ":Day0EphemeralTry";
    public const string ServiceSigner = SectionName + ":ServiceSigner";
}
