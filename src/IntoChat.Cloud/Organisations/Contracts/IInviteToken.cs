using DigitalBrain.Access;
using IntoChat.OS;

namespace IntoChat.Cloud.Organisations;

[DigitalBrain.Contracts.NeuronAlias("organisations.invite-token"), DigitalBrain.Contracts.DefaultNeuronType("organisations.invite-token")]
public interface IInviteToken
{
    const string Type = "organisations.invite-token";

    static string Key(string tokenHash) => $"{NeuronKeys.SystemShard(tokenHash)}/invite/{tokenHash}";

    [Internal]
    Task Register(string orgBrain, Guid inviteId, DateTimeOffset expiresAt);

    // Public: the key is the token hash, so only a holder of the token can address it; the answer names
    // the organisation brain the invite lives in and nothing else.
    [Public]
    Task<InviteTokenRef?> Resolve();

    [Internal]
    Task Consume();
}
