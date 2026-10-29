namespace IntoChat.Cloud.Auth.Core;

/// <summary>Stable error strings carried over from W4.</summary>
public static class AuthErrors
{
    public const string EmailCodeInvalid = "EMAIL_CODE_INVALID";
    public const string EmailCodeExpired = "EMAIL_CODE_EXPIRED";
    public const string EmailCodeLocked = "EMAIL_CODE_LOCKED";
    public const string EmailCodeRateLimited = "EMAIL_CODE_RATE_LIMITED";
    public const string EmailCodeNotFound = "EMAIL_CODE_NOT_FOUND";
    public const string PasswordResetTicketInvalid = "PASSWORD_RESET_TICKET_INVALID";
    public const string UserConsentNotGranted = "USER_CONSENT_NOT_GRANTED";
    public const string AuthenticatorCodeInvalid = "AUTHENTICATOR_CODE_INVALID";
    public const string RecoveryCodeInvalid = "RECOVERY_CODE_INVALID";
    public const string AuthenticatorNotEnabled = "AUTHENTICATOR_NOT_ENABLED";
    public const string ReauthenticationRequired = "REAUTHENTICATION_REQUIRED";
}

/// <summary>The uniform 202 body (W4 D10): identical for real and decoy subjects.</summary>
public sealed record CodeIssued(string CodeId, DateTimeOffset ResendAvailableAt, DateTimeOffset ExpiresAt);

public enum IssueKind
{
    Issued,
    RateLimited,
}

public sealed record IssueResult(CodeRecord Record, IssueKind Kind, string? PlainCode, CodeIssued? Issued, DateTimeOffset? RetryAt);

public enum VerifyKind
{
    Success,
    Invalid,
    Expired,
    Locked,
    RateLimited,
    NotFound,
}

/// <param name="Record">The updated record to persist.</param>
/// <param name="Kind">Outcome.</param>
/// <param name="AttemptsLeft">Set for Invalid.</param>
/// <param name="Reissued">Set for Expired and Locked: the replacement code to announce.</param>
/// <param name="PlainCode">The replacement plain code, for the mail sender only.</param>
/// <param name="RetryAt">Set for RateLimited.</param>
/// <param name="Reason">For RateLimited, the original reason (expired or locked).</param>
public sealed record VerifyResult(
    CodeRecord Record,
    VerifyKind Kind,
    int? AttemptsLeft = null,
    CodeIssued? Reissued = null,
    string? PlainCode = null,
    DateTimeOffset? RetryAt = null,
    string? Reason = null)
{
    public string? Error => Kind switch
    {
        VerifyKind.Success => null,
        VerifyKind.Invalid => AuthErrors.EmailCodeInvalid,
        VerifyKind.Expired => AuthErrors.EmailCodeExpired,
        VerifyKind.Locked => AuthErrors.EmailCodeLocked,
        VerifyKind.RateLimited => AuthErrors.EmailCodeRateLimited,
        _ => AuthErrors.EmailCodeNotFound,
    };
}

public enum TicketResult
{
    Redeemed,
    TicketInvalid,
}
