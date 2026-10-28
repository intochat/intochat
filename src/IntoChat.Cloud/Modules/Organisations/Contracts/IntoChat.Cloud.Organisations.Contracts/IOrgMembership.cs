using DigitalBrain.Access;
using IntoChat.Cloud.Mail;

namespace IntoChat.Cloud.Organisations;

[DigitalBrain.Contracts.NeuronAlias("organisations.membership"), DigitalBrain.Contracts.DefaultNeuronType("organisations.membership")]
public interface IOrgMembership
{
    const string Type = "organisations.membership";

    static string Key(string principalId) => $"{SystemShard.BrainOf(principalId)}/membership/{principalId}";

    [Internal]
    Task<OrgMembershipState?> Get();

    [Internal]
    Task Set(string orgId, string orgBrain, string emailHash, string personalOrgBrain, string? expectedOrgId = null);
}
