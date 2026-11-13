namespace IntoChat.Cloud.Auth;

public enum AuthAuditKind
{
    SignUp,
    LoginSucceeded,
    LoginFailed,
    CodeFailed,
    CodeLocked,
    TotpEnabled,
    TotpDisabled,
    RecoveryCodesRegenerated,
    SessionRevoked,
    PasswordReset,
    EmailChanged,
    GoogleLinked,
    LicenseState,
    MailRateLimited,
    MailFailed,
    MailNotConfigured,
}

// One security event. Detail is a closed vocabulary; entries never hold codes, tokens or email addresses.
public sealed record AuthAuditEntry(
    DateTimeOffset At,
    string? PrincipalId,
    AuthAuditKind Kind,
    string Outcome,
    string IpPrefix,
    string UserAgentFamily,
    string? Detail);
