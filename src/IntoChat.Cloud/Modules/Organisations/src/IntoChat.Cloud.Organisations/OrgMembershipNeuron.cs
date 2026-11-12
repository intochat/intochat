using DigitalBrain;
using DigitalBrain.Neurons;

namespace IntoChat.Cloud.Organisations;

[Neuron(IOrgMembership.Type)]
internal sealed class OrgMembershipNeuron : Neuron<OrgMembershipState>, IOrgMembership
{
    private bool Exists => State.OrgId.Length > 0;

    public Task<OrgMembershipState?> Get() => Task.FromResult(Exists ? Clone(State) : null);

    public async Task Set(string orgId, string orgBrain, string emailHash, string personalOrgBrain, string? expectedOrgId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgBrain);
        if (Exists && State.OrgId != orgId && State.OrgId != expectedOrgId) { throw new OrganisationException("ALREADY_IN_ORGANISATION"); }
        var next = new OrgMembershipState
        {
            OrgId = orgId,
            OrgBrain = orgBrain,
            EmailHash = emailHash,
            PersonalOrgBrain = personalOrgBrain,
        };
        await Save(next, new OrganisationBookkeeping(orgId, "membership"));
    }

    private static OrgMembershipState Clone(OrgMembershipState state) => new()
    {
        OrgId = state.OrgId,
        OrgBrain = state.OrgBrain,
        EmailHash = state.EmailHash,
        PersonalOrgBrain = state.PersonalOrgBrain,
    };
}
