using DigitalBrain.Access;

namespace IntoChat.Cloud.Organisations;

[DigitalBrain.Contracts.NeuronAlias("organisations.invites"), DigitalBrain.Contracts.DefaultNeuronType("organisations.invites")]
public interface IOrganisationInvites
{
    const string Type = "organisations.invites";

    static string Key(string orgBrain) => $"{orgBrain}/organisation/invites";

    [Public]
    Task<InviteResult[]> InviteMembers(string[] emails, OrganisationRole role);

    [Public]
    Task RevokeInvite(Guid inviteId);

    [Public]
    Task<PendingInviteView[]> GetPendingInvites();

    [Internal]
    Task<InvitePreview?> PreviewByHash(string tokenHash);

    [Public]
    Task<AcceptResult> Accept(string token);

    [Public]
    Task Decline(string token);
}
