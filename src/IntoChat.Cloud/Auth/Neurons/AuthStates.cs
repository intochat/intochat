
namespace IntoChat.Cloud.Auth.Neurons;

// The organisation audit log is the user's data: exportable (Ruling 4).
[DigitalBrain.State.ExportableState]
internal sealed class AuthAuditState
{
    public AuthAuditEntry[] Entries { get; set; } = [];
}

// Installation-wide events without a principal (licence state, mail failures): not exportable.
internal sealed class AuthSystemAuditState
{
    public AuthAuditEntry[] Entries { get; set; } = [];
}

// One email-code slot: hashes and counters only, never a plain code. Delivery keeps the address so a reissue or resend
// can mail the replacement; system shard, not exportable.
internal sealed class AuthCodeState
{
    public IntoChat.Cloud.Auth.Core.CodePurpose Purpose { get; set; }
    public string SubjectHash { get; set; } = "";
    public string Id { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public int AttemptsLeft { get; set; }
    public bool Active { get; set; }
    public bool IsDecoy { get; set; }
    public bool Consumed { get; set; }
    public DateTimeOffset[] SendTimes { get; set; } = [];
    public string? TicketHash { get; set; }
    public DateTimeOffset? TicketExpiresAt { get; set; }
    public bool TicketUsed { get; set; }
    public string[] AllowedMethods { get; set; } = [];
    public string AmrOrigin { get; set; } = "";
    public CodeDelivery? Delivery { get; set; }

    public IntoChat.Cloud.Auth.Core.CodeRecord ToRecord() => new(
        Purpose, SubjectHash, Id, CodeHash, ExpiresAt, SentAt, AttemptsLeft, Active, IsDecoy, Consumed, SendTimes, TicketHash, TicketExpiresAt, TicketUsed);

    public static AuthCodeState From(IntoChat.Cloud.Auth.Core.CodeRecord record, CodeDelivery? delivery, string[] allowedMethods, string amrOrigin) => new()
    {
        Purpose = record.Purpose,
        SubjectHash = record.SubjectHash,
        Id = record.Id,
        CodeHash = record.CodeHash,
        ExpiresAt = record.ExpiresAt,
        SentAt = record.SentAt,
        AttemptsLeft = record.AttemptsLeft,
        Active = record.Active,
        IsDecoy = record.IsDecoy,
        Consumed = record.Consumed,
        SendTimes = [.. record.SendTimes],
        TicketHash = record.TicketHash,
        TicketExpiresAt = record.TicketExpiresAt,
        TicketUsed = record.TicketUsed,
        AllowedMethods = allowedMethods,
        AmrOrigin = amrOrigin,
        Delivery = delivery,
    };
}

public enum EmailIndexStatus
{
    None,
    Pending,
    Verified,
    ChangePending,
}

// The global email uniqueness index entry for one email hash (system shard, not exportable).
public sealed class AuthEmailIndexState
{
    public EmailIndexStatus Status { get; set; }
    public string? PrincipalId { get; set; }
    public string? AccountId { get; set; }
    public string? BrainId { get; set; }
    public string? PendingPasswordHash { get; set; }
    public string? PendingDisplayName { get; set; }
    public string? PendingLanguage { get; set; }
    public DateTimeOffset? ConsentAt { get; set; }
    public string? GoogleSub { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset? ChangeExpiresAt { get; set; }
    public string? PreviousEmailHash { get; set; }
    public string? ChangeCodeId { get; set; }
    public bool ChangeProved { get; set; }
}

// Per-principal credential state: never exportable; the TOTP secret is sealed, recovery codes are hashed.
internal sealed class AuthCredentialState
{
    public string? PasswordHash { get; set; }
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DigitalBrain.Capabilities.Secrets.SealedSecret? TotpPending { get; set; }
    public DigitalBrain.Capabilities.Secrets.SealedSecret? TotpSealed { get; set; }
    public long TotpLastStep { get; set; }
    public string[] RecoveryHashes { get; set; } = [];
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public string? EmailHash { get; set; }
}

internal sealed class AuthSession
{
    public string SidHash { get; set; } = "";
    public string Handle { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Label { get; set; } = "";
    public DateTimeOffset? RevokedAt { get; set; }
    // The member the session was issued for (identity@1 Validate answers it); account, brain and role at issue time.
    public string PrincipalId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string BrainId { get; set; } = "";
    public string Role { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

// The sessions table (W4 D11) of one principal: hashes of session ids only, not exportable.
internal sealed class AuthSessionsState
{
    public AuthSession[] Sessions { get; set; } = [];
}

// Per-principal profile: preferences, avatar picture and the welcome-mail marker; never exportable or logged.
internal sealed class AuthProfileState
{
    public string? DisplayName { get; set; }
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "system";
    public string TimeZone { get; set; } = "UTC";
    public DateTimeOffset? WelcomeClaimedAt { get; set; }
    public string? AvatarContentType { get; set; }
    public byte[]? AvatarBytes { get; set; }
}
