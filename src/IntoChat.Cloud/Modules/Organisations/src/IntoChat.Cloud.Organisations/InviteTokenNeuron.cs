using DigitalBrain;
using DigitalBrain.Neurons;

namespace IntoChat.Cloud.Organisations;

[Neuron(IInviteToken.Type)]
internal sealed class InviteTokenNeuron(TimeProvider clock) : Neuron<InviteTokenState>, IInviteToken
{
    private bool Exists => State.OrgBrain.Length > 0;

    public async Task Register(string orgBrain, Guid inviteId, DateTimeOffset expiresAt)
    {
        if (Exists) { return; }
        await Save(new InviteTokenState { OrgBrain = orgBrain, InviteId = inviteId, ExpiresAt = expiresAt }, new OrganisationBookkeeping(orgBrain, "invite-token"));
    }

    public Task<InviteTokenRef?> Resolve()
    {
        if (!Exists || State.Consumed || State.ExpiresAt <= clock.GetUtcNow()) { return Task.FromResult<InviteTokenRef?>(null); }
        return Task.FromResult<InviteTokenRef?>(new InviteTokenRef(State.OrgBrain, State.InviteId, State.ExpiresAt));
    }

    public async Task Consume()
    {
        if (!Exists || State.Consumed) { return; }
        await Save(new InviteTokenState
        {
            OrgBrain = State.OrgBrain,
            InviteId = State.InviteId,
            ExpiresAt = State.ExpiresAt,
            Consumed = true,
        }, new OrganisationBookkeeping(State.OrgBrain, "invite-token-consumed"));
    }
}
