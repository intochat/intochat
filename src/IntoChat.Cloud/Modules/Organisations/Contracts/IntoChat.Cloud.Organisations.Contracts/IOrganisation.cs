using DigitalBrain.Access;

namespace IntoChat.Cloud.Organisations;

[DigitalBrain.Contracts.NeuronAlias("organisations.organisation"), DigitalBrain.Contracts.DefaultNeuronType("organisations.organisation")]
public interface IOrganisation
{
    const string Type = "organisations.organisation";

    static string Key(string orgBrain) => $"{orgBrain}/organisation";

    [Public]
    Task<OrganisationState> Get();

    [Public]
    Task Rename(string name);

    [Public]
    Task ChangeRole(string principalId, OrganisationRole role);

    [Public]
    Task RemoveMember(string principalId);

    [Internal]
    Task<bool> CreatePersonal(PersonalOrganisationRequest request);

    [Internal]
    Task<OrganisationRole?> RoleOf(string principalId);

    [Internal]
    Task<OrganisationState> Read();

    [Internal]
    Task AddMember(OrganisationMember member);

    [Internal]
    Task Dissolve();

    [Internal]
    Task Revive(string name, OrganisationMember admin);

    [Internal]
    Task RemoveMemberRecord(string principalId);
}
