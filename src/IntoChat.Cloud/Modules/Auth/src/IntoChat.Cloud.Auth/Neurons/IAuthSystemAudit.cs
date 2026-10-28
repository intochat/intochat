using DigitalBrain.Access;
using IntoChat.Cloud.Mail;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Contracts.NeuronAlias("auth.system-audit"), DigitalBrain.Contracts.DefaultNeuronType("auth.system-audit")]
public interface IAuthSystemAudit
{
    const string Type = "auth.system-audit";

    static string Key() => SystemShard.BrainOf("auth-system") + "/auth/system-audit";

    [Internal]
    Task Append(AuthAuditEntry entry);

    [Internal]
    Task<AuthAuditEntry[]> Read(AuthAuditKind? kind, int take);
}
