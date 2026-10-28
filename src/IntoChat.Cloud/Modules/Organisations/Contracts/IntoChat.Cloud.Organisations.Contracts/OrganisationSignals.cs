using DigitalBrain;

namespace IntoChat.Cloud.Organisations;

[Signal("organisations.organisation-created")]
public sealed record OrganisationCreated(
    string OrgId,
    string Name,
    string OwnerPrincipalId) : DigitalBrain.Signal;

[Signal("organisations.member-invited")]
public sealed record MemberInvited(
    string OrgId,
    string InviteId,
    string EmailHash,
    string EmailMasked,
    OrganisationRole Role) : DigitalBrain.Signal;

[Signal("organisations.invite-revoked")]
public sealed record InviteRevoked(
    string OrgId,
    string InviteId) : DigitalBrain.Signal;

[Signal("organisations.invite-accepted")]
public sealed record InviteAccepted(
    string OrgId,
    string InviteId,
    string PrincipalId) : DigitalBrain.Signal;

[Signal("organisations.member-removed")]
public sealed record MemberRemoved(
    string OrgId,
    string PrincipalId) : DigitalBrain.Signal;

[Signal("organisations.role-changed")]
public sealed record RoleChanged(
    string OrgId,
    string PrincipalId,
    OrganisationRole Role,
    OrganisationRole PreviousRole) : DigitalBrain.Signal;
