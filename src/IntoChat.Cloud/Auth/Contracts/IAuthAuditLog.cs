using DigitalBrain.Access;

namespace IntoChat.Cloud.Auth;

// The organisation's security audit log, kept in the organisation brain. Admins of the organisation read it;
// only the Auth module appends to it.
[DigitalBrain.Contracts.NeuronAlias("auth.audit"), DigitalBrain.Contracts.DefaultNeuronType("auth.audit")]
public interface IAuthAuditLog
{
    const string Type = "auth.audit";

    static string Key(string orgBrain) => $"{orgBrain}/auth/audit";

    [Public]
    Task<AuthAuditEntry[]> Read(int take);

    [Internal]
    Task Append(AuthAuditEntry entry);
}
