using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.Cloud.Mail;

namespace IntoChat.Cloud.Organisations;

// Personal organisations. Compute placement from the platform grain is not ported: topology decides where the neuron runs.
[Neuron(IOrganisationDirectory.Type)]
internal sealed class OrganisationDirectoryNeuron(IRecipientHasher hasher, INeuronProxies proxies) : Neuron, IOrganisationDirectory
{
    internal const int MaxEmailLength = 255;

    public async Task<PersonalOrganisationResult> EnsurePersonal(PersonalOrganisationRequest request)
    {
        Validate(request);
        var membership = proxies.Get<IOrgMembership>(OrgIds.Of(IOrgMembership.Type, IOrgMembership.Key(request.PrincipalId)));
        var existing = await membership.Get();
        if (existing is not null)
        {
            var role = await proxies.Get<IOrganisation>(OrgIds.Of(IOrganisation.Type, IOrganisation.Key(existing.OrgBrain))).RoleOf(request.PrincipalId);
            return new PersonalOrganisationResult(existing.OrgId, role ?? OrganisationRole.Member, false);
        }

        var organisation = proxies.Get<IOrganisation>(OrgIds.Of(IOrganisation.Type, IOrganisation.Key(request.BrainId)));
        var created = await organisation.CreatePersonal(request);
        var emailHash = hasher.Hash(InviteHasher.EmailPurpose, request.Email);
        await membership.Set(request.BrainId, request.BrainId, emailHash, request.BrainId);
        return new PersonalOrganisationResult(request.BrainId, OrganisationRole.Admin, created);
    }

    private void Validate(PersonalOrganisationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = OrgIds.Of(IOrganisationDirectory.Type, IOrganisationDirectory.Key(request.PrincipalId));
        if (Id != expected) { throw new OrganisationException("PRINCIPAL_KEY_MISMATCH"); }
        if (!BrainId.IsValid(request.BrainId) || string.IsNullOrWhiteSpace(request.PrincipalId)
            || string.IsNullOrWhiteSpace(request.AccountId) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new OrganisationException("REQUEST_INVALID");
        }
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > MaxEmailLength || !request.Email.Contains('@'))
        {
            throw new OrganisationException("EMAIL_INVALID");
        }
        if (request.Language is not ("en" or "ru")) { throw new OrganisationException("LANGUAGE_INVALID"); }
    }
}
