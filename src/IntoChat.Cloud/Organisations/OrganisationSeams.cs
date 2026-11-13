using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Mail;

namespace IntoChat.Cloud.Organisations;

// Membership lookup and removal go through identity@1. The kernel binds this over IIdentity inside the module call (09-09).
public interface IOrganisationIdentity
{
    Task<Member?> Membership(string brainId, string principalId, CancellationToken cancellationToken = default);

    Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default);

    Task RemovePrincipalMember(string brainId, string principalId, CancellationToken cancellationToken = default);
}

// Invite mail. The kernel adapter uses IMailSender; this project does not name template types.
public interface IOrganisationMail
{
    Task<bool> SendTeamInvite(string email, string inviterName, string organisationName, string actionUrl, CancellationToken cancellationToken = default);

    Task SendInviteAccepted(string email, string memberName, string organisationName, string actionUrl, CancellationToken cancellationToken = default);
}

internal sealed class UnconfiguredOrganisationMail : IOrganisationMail
{
    public Task<bool> SendTeamInvite(string email, string inviterName, string organisationName, string actionUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task SendInviteAccepted(string email, string memberName, string organisationName, string actionUrl, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal interface IPersonalOrganisationGuard
{
    Task<bool> CanDissolveAsync(string personalOrgBrain, string principalId);
}

internal sealed class AllowAllPersonalOrganisationGuard : IPersonalOrganisationGuard
{
    public Task<bool> CanDissolveAsync(string personalOrgBrain, string principalId) => Task.FromResult(true);
}

internal static class InviteHasher
{
    public const string EmailPurpose = "member-email";

    public static string HashEmail(this IRecipientHasher hasher, string email) => hasher.Hash(EmailPurpose, email);

    public static bool HashesEqual(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
