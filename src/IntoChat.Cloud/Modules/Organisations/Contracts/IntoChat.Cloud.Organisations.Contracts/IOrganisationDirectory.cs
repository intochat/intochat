using IntoChat.Cloud.Mail;
using DigitalBrain.Access;

namespace IntoChat.Cloud.Organisations;

[DigitalBrain.Contracts.NeuronAlias("organisations.directory"), DigitalBrain.Contracts.DefaultNeuronType("organisations.directory")]
public interface IOrganisationDirectory
{
    const string Type = "organisations.directory";

    static string Key(string principalId) => $"{SystemShard.BrainOf(principalId)}/organisation-directory/{principalId}";

    [Internal]
    Task<PersonalOrganisationResult> EnsurePersonal(PersonalOrganisationRequest request);
}
