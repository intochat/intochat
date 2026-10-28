using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Licensing;

// What the host licence gate learns of Auth's licence token (the offline ES256 token of IntoChatOS:Modules:auth:Options:License,
// verified against the keys embedded in this package). The kernel's ILicenseCheck adapter maps this result onto its own type
// (09-09), so this assembly never references the os framework.
public sealed record AuthLicenseResult(bool Ok, string? Holder, DateTimeOffset? ExpiresAt, string? Failure);

public sealed class AuthLicenseGate
{
    private readonly LicenseVerifier verifier;

    public AuthLicenseGate()
        : this(LicenseVerifier.WithEmbeddedKeys())
    {
    }

    internal AuthLicenseGate(LicenseVerifier verifier) => this.verifier = verifier;

    // The configuration key the licence token is read from.
    public static string TokenKey => AuthModule.OptionsSection + ":" + nameof(AuthModuleOptions.License);

    public AuthLicenseResult Check(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthLicenseResult(false, null, null, LicenseFailure.Missing);
        }

        var result = verifier.Verify(token);
        return result.Ok
            ? new AuthLicenseResult(true, result.Org, result.ExpiresAt, null)
            : new AuthLicenseResult(false, null, null, result.Reason is LicenseFailure.Expired ? LicenseFailure.Expired : LicenseFailure.Invalid);
    }
}
