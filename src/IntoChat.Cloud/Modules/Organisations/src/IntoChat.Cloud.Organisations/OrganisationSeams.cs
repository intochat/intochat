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

// Same SHA-256 / base64url rules as IntoChat.Cloud.Mail.Templates.InviteTokens. Duplicated so this project does not reference Templates.
internal static class InviteTokens
{
    public const int MaxTokenLength = 64;
    public const string LinkPath = "/invite#";

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsWellFormed(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) { return false; }
        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { return false; }
        }
        return true;
    }

    public static string LinkFor(string publicBaseUrl, string token) => publicBaseUrl.TrimEnd('/') + LinkPath + token;

    public static bool HashesEqual(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

internal static class EmailMask
{
    public static string Mask(string email)
    {
        if (string.IsNullOrEmpty(email)) { return "***"; }
        var at = email.IndexOf('@');
        return at < 1 ? "***" : $"{email[0]}***@{email[(at + 1)..]}";
    }
}

internal static class InviteHasher
{
    public const string EmailPurpose = "member-email";

    public static string HashEmail(this IRecipientHasher hasher, string email) => hasher.Hash(EmailPurpose, email);
}
