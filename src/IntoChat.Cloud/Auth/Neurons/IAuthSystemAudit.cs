using DigitalBrain.Access;
using IntoChat.OS;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Contracts.NeuronAlias("auth.system-audit"), DigitalBrain.Contracts.DefaultNeuronType("auth.system-audit")]
public interface IAuthSystemAudit
{
    const string Type = "auth.system-audit";

    static string Key() => NeuronKeys.SystemShard("auth-system") + "/auth/system-audit";

    [Internal]
    Task Append(AuthAuditEntry entry);

    [Internal]
    Task<AuthAuditEntry[]> Read(AuthAuditKind? kind, int take);
}
