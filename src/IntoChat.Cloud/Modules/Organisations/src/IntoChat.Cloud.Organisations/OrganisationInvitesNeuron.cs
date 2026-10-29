using MailAddress = System.Net.Mail.MailAddress;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Spi;
using IntoChat.Cloud.Mail;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Organisations;

[Neuron(IOrganisationInvites.Type)]
internal sealed class OrganisationInvitesNeuron(
    OrganisationsModuleOptions options,
    IRecipientHasher hasher,
    IOrganisationMail mail,
    IOrganisationIdentity identity,
    IPersonalOrganisationGuard personalGuard,
    IOrganisationHost host,
    INeuronProxies proxies,
    TimeProvider clock,
    ILogger<OrganisationInvitesNeuron> logger) : Neuron<InviteBook>, IOrganisationInvites
{
    private const int MaxEmailLength = 255;

    private string OrgBrain => Id.Brain.Value;

    public async Task<InviteResult[]> InviteMembers(string[] emails, OrganisationRole role)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var organisation = await RequireAdminAsync();
        Validate(emails, role);
        var now = clock.GetUtcNow();
        var caller = Caller();
        var inviter = organisation.Members.First(member => member.PrincipalId == caller.PrincipalId);
        var next = State.Clone();
        var results = new List<InviteResult>();
        var signals = new List<Signal>();
        var outgoing = new List<(PendingInvite Invite, string Email, string Token)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in emails)
        {
            var email = raw.Trim();
            var emailHash = hasher.HashEmail(email);
            var masked = EmailMask.Mask(email);
            if (!seen.Add(emailHash)) { continue; }
            if (organisation.Members.Any(member => hasher.HashEmail(member.Email) == emailHash))
            {
                results.Add(new InviteResult(masked, null, "INVITE_ALREADY_MEMBER", null));
                continue;
            }

            var pending = next.Invites.FirstOrDefault(invite => invite.Status == InviteStatus.Pending && invite.ExpiresAt > now && invite.EmailHash == emailHash);
            if (pending is not null && now - pending.LastSentAt < options.InviteResendCooldown)
            {
                results.Add(new InviteResult(masked, pending.InviteId, "INVITE_RATE_LIMITED", pending.LastSentAt + options.InviteResendCooldown));
                continue;
            }

            var token = InviteTokens.NewToken();
            var tokenHash = InviteTokens.Hash(token);
            var expiresAt = now + options.InviteLifetime;
            string outcome;
            if (pending is null)
            {
                pending = new PendingInvite
                {
                    InviteId = Guid.NewGuid(),
                    EmailHash = emailHash,
                    EmailMasked = masked,
                    CreatedAt = now,
                    InvitedBy = caller.PrincipalId,
                };
                next.Invites = [.. next.Invites, pending];
                outcome = "INVITED";
            }
            else
            {
                await host.InModuleCall(() => Token(pending.TokenHash).Consume());
                outcome = "RESENT";
            }

            pending.Role = role;
            pending.TokenHash = tokenHash;
            pending.ExpiresAt = expiresAt;
            pending.LastSentAt = now;
            pending.LastSendFailed = false;
            await host.InModuleCall(() => Token(tokenHash).Register(OrgBrain, pending.InviteId, expiresAt));
            signals.Add(new MemberInvited(organisation.OrgId, pending.InviteId.ToString("N"), emailHash, masked, role));
            outgoing.Add((pending, email, token));
            results.Add(new InviteResult(masked, pending.InviteId, outcome, null));
        }

        if (signals.Count == 0) { return [.. results]; }
        await Save(next, signals);

        var failed = new List<Guid>();
        foreach (var (invite, email, token) in outgoing)
        {
            if (!await SendInviteAsync(email, inviter.DisplayName, organisation.Name, token)) { failed.Add(invite.InviteId); }
        }

        if (failed.Count > 0)
        {
            var flagged = State.Clone();
            foreach (var invite in flagged.Invites.Where(invite => failed.Contains(invite.InviteId))) { invite.LastSendFailed = true; }
            await Save(flagged, new OrganisationBookkeeping(organisation.OrgId, "invite-send-failed"));
        }

        return [.. results];
    }

    public async Task RevokeInvite(Guid inviteId)
    {
        var organisation = await RequireAdminAsync();
        var invite = State.Invites.FirstOrDefault(candidate => candidate.InviteId == inviteId && candidate.Status == InviteStatus.Pending)
            ?? throw new OrganisationException("INVITE_NOT_FOUND");
        await host.InModuleCall(() => Token(invite.TokenHash).Consume());
        var next = State.Clone();
        next.Invites.Single(candidate => candidate.InviteId == inviteId).Status = InviteStatus.Revoked;
        await Save(next, new InviteRevoked(organisation.OrgId, inviteId.ToString("N")));
    }

    public async Task<PendingInviteView[]> GetPendingInvites()
    {
        await RequireAdminAsync();
        var now = clock.GetUtcNow();
        return [.. State.Invites
            .Where(invite => invite.Status == InviteStatus.Pending && invite.ExpiresAt > now)
            .Select(invite => new PendingInviteView(invite.InviteId, invite.EmailMasked, invite.Role, invite.CreatedAt, invite.ExpiresAt, invite.LastSentAt, invite.LastSendFailed))];
    }

    public async Task<InvitePreview?> PreviewByHash(string tokenHash)
    {
        var invite = State.Invites.FirstOrDefault(candidate => candidate.TokenHash == tokenHash);
        if (invite is null || invite.Status != InviteStatus.Pending || invite.ExpiresAt <= clock.GetUtcNow()) { return null; }
        var organisation = await host.InModuleCall(() => Organisation().Read());
        if (organisation.Status != OrganisationStatus.Active) { return null; }
        var inviterName = organisation.Members.FirstOrDefault(member => member.PrincipalId == invite.InvitedBy)?.DisplayName ?? organisation.Name;
        return new InvitePreview(organisation.Name, inviterName, invite.Role, invite.ExpiresAt);
    }

    public async Task<AcceptResult> Accept(string token)
    {
        var invite = FindPending(token);
        var caller = Caller();
        var membershipNeuron = proxies.Get<IOrgMembership>(OrgIds.Of(IOrgMembership.Type, IOrgMembership.Key(caller.PrincipalId)));
        var membership = await host.InModuleCall(() => membershipNeuron.Get()) ?? throw new OrganisationException("MEMBERSHIP_CONFLICT");
        if (!InviteTokens.HashesEqual(membership.EmailHash, invite.EmailHash)) { throw new OrganisationException("INVITE_EMAIL_MISMATCH"); }

        var target = await host.InModuleCall(() => Organisation().Read());
        if (target.Status != OrganisationStatus.Active) { throw new OrganisationException("INVITE_NOT_FOUND"); }

        var personal = proxies.Get<IOrganisation>(OrgIds.Of(IOrganisation.Type, IOrganisation.Key(membership.PersonalOrgBrain)));
        var personalState = await host.InModuleCall(() => personal.Read());
        var self = personalState.Members.FirstOrDefault(member => member.PrincipalId == caller.PrincipalId) ?? throw new OrganisationException("MEMBERSHIP_CONFLICT");
        var alreadyMoved = membership.OrgBrain == OrgBrain;
        if (!alreadyMoved)
        {
            var onlyMember = personalState.Status == OrganisationStatus.Active && personalState.Members.Length == 1;
            if (membership.OrgBrain != membership.PersonalOrgBrain || !onlyMember
                || !await personalGuard.CanDissolveAsync(membership.PersonalOrgBrain, caller.PrincipalId))
            {
                throw new OrganisationException("MEMBERSHIP_CONFLICT");
            }
        }

        var owner = await host.InModuleCall(() => identity.Membership(OrgBrain, target.PersonalOwnerPrincipalId)) ?? throw new OrganisationException("ORGANISATION_NOT_FOUND");
        var now = clock.GetUtcNow();
        await host.InModuleCall(() => identity.EnsureMember(new Member
        {
            PrincipalId = caller.PrincipalId,
            AccountId = owner.AccountId,
            BrainId = OrgBrain,
            Role = MemberRole.Member,
            DisplayName = self.DisplayName,
            JoinedAt = now,
        }));
        await host.InModuleCall(() => Organisation().AddMember(new OrganisationMember
        {
            PrincipalId = caller.PrincipalId,
            DisplayName = self.DisplayName,
            Email = self.Email,
            Role = invite.Role,
            JoinedAt = now,
        }));
        if (!alreadyMoved)
        {
            await host.InModuleCall(() => membershipNeuron.Set(OrgBrain, OrgBrain, membership.EmailHash, membership.PersonalOrgBrain, membership.OrgId));
        }

        await host.InModuleCall(personal.Dissolve);
        var next = State.Clone();
        next.Invites.Single(candidate => candidate.InviteId == invite.InviteId).Status = InviteStatus.Accepted;
        await Save(next, new InviteAccepted(target.OrgId, invite.InviteId.ToString("N"), caller.PrincipalId));
        await host.InModuleCall(() => Token(invite.TokenHash).Consume());
        await NotifyInviterAsync(target, invite, self.DisplayName);
        return new AcceptResult(target.OrgId, invite.Role);
    }

    public async Task Decline(string token)
    {
        var invite = FindPending(token);
        Caller();
        var next = State.Clone();
        next.Invites.Single(candidate => candidate.InviteId == invite.InviteId).Status = InviteStatus.Declined;
        await Save(next, new InviteDeclined(OrgBrain, invite.InviteId.ToString("N")));
        await host.InModuleCall(() => Token(invite.TokenHash).Consume());
    }

    private PendingInvite FindPending(string token)
    {
        var now = clock.GetUtcNow();
        var tokenHash = InviteTokens.IsWellFormed(token) ? InviteTokens.Hash(token) : "";
        return State.Invites.FirstOrDefault(candidate => tokenHash.Length > 0 && candidate.TokenHash == tokenHash
                && candidate.Status == InviteStatus.Pending && candidate.ExpiresAt > now)
            ?? throw new OrganisationException("INVITE_NOT_FOUND");
    }

    private async Task NotifyInviterAsync(OrganisationState organisation, PendingInvite invite, string memberName)
    {
        var inviter = organisation.Members.FirstOrDefault(member => member.PrincipalId == invite.InvitedBy);
        if (inviter is null) { return; }
        try
        {
            await mail.SendInviteAccepted(inviter.Email, memberName, organisation.Name, options.PublicBaseUrl, CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "The inviter was not told that the invite was accepted.");
        }
    }

    private IOrganisation Organisation() => proxies.Get<IOrganisation>(OrgIds.Of(IOrganisation.Type, IOrganisation.Key(OrgBrain)));

    private IInviteToken Token(string tokenHash) => proxies.Get<IInviteToken>(OrgIds.Of(IInviteToken.Type, IInviteToken.Key(tokenHash)));

    private async Task<OrganisationState> RequireAdminAsync()
    {
        var caller = Caller();
        var organisation = await host.InModuleCall(() => Organisation().Read());
        if (organisation.Status == OrganisationStatus.Dissolved) { throw new OrganisationException("ORGANISATION_DISSOLVED"); }
        var role = organisation.Members.FirstOrDefault(member => member.PrincipalId == caller.PrincipalId)?.Role;
        if (role != OrganisationRole.Admin) { throw new UnauthorizedAccessException("Only an administrator may manage invites."); }
        return organisation;
    }

    private CapabilityCaller Caller() => host.CurrentCaller ?? throw new UnauthorizedAccessException("The caller is not signed in.");

    private void Validate(string[] emails, OrganisationRole role)
    {
        if (emails.Length < 1 || emails.Length > options.MaxInvitesPerRequest) { throw new OrganisationException("INVITE_COUNT_INVALID"); }
        if (!Enum.IsDefined(role)) { throw new OrganisationException("ROLE_INVALID"); }
        foreach (var email in emails)
        {
            var trimmed = email?.Trim() ?? "";
            if (trimmed.Length == 0 || trimmed.Length > MaxEmailLength || !MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed)
            {
                throw new OrganisationException("EMAIL_INVALID");
            }
        }
    }

    private async Task<bool> SendInviteAsync(string email, string inviterName, string organisationName, string token)
    {
        try
        {
            return await mail.SendTeamInvite(email, inviterName, organisationName, InviteTokens.LinkFor(options.PublicBaseUrl, token), CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "The invitation email was not sent.");
            return false;
        }
    }
}
