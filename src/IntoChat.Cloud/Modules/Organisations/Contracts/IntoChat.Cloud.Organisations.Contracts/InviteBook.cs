using DigitalBrain;

namespace IntoChat.Cloud.Organisations;

public enum InviteStatus
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Revoked = 3,
}

public sealed class PendingInvite
{
    public Guid InviteId { get; set; }
    public string EmailHash { get; set; } = "";
    public string EmailMasked { get; set; } = "";
    public OrganisationRole Role { get; set; }
    public string TokenHash { get; set; } = "";
    public InviteStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public string InvitedBy { get; set; } = "";
    public bool LastSendFailed { get; set; }

    public PendingInvite Clone() => (PendingInvite)MemberwiseClone();
}

public sealed class InviteBook
{
    public PendingInvite[] Invites { get; set; } = [];

    public InviteBook Clone() => new() { Invites = [.. Invites.Select(invite => invite.Clone())] };
}

public sealed class InviteTokenState
{
    public string OrgBrain { get; set; } = "";
    public Guid InviteId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

public sealed record InviteResult(
    string EmailMasked,
    Guid? InviteId,
    string Outcome,
    DateTimeOffset? RetryAt);

public sealed record PendingInviteView(
    Guid InviteId,
    string EmailMasked,
    OrganisationRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSentAt,
    bool LastSendFailed);

public sealed record InvitePreview(
    string OrganisationName,
    string InviterName,
    OrganisationRole Role,
    DateTimeOffset ExpiresAt);

public sealed record AcceptResult(
    string OrganisationId,
    OrganisationRole Role);

public sealed record InviteTokenRef(
    string OrgBrain,
    Guid InviteId,
    DateTimeOffset ExpiresAt);
