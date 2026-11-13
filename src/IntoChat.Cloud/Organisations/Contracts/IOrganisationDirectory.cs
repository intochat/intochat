using DigitalBrain.Access;
using IntoChat.OS;

namespace IntoChat.Cloud.Organisations;

[DigitalBrain.Contracts.NeuronAlias("organisations.directory"), DigitalBrain.Contracts.DefaultNeuronType("organisations.directory")]
public interface IOrganisationDirectory
{
    const string Type = "organisations.directory";

    static string Key(string principalId) => $"{NeuronKeys.SystemShard(principalId)}/organisation-directory/{principalId}";

    [Internal]
    Task<PersonalOrganisationResult> EnsurePersonal(PersonalOrganisationRequest request);
}
