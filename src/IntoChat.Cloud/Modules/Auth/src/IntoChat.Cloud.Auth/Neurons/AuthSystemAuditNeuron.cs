
namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neuron("auth.system-audit")]
internal sealed class AuthSystemAuditNeuron(
    AuthModuleOptions options,
    TimeProvider clock) : AuthNeuron<AuthSystemAuditState>, IAuthSystemAudit
{
    public Task Append(AuthAuditEntry entry) =>
        SaveQuietly(new AuthSystemAuditState { Entries = AuditPruning.Prune(Snapshot.Entries, entry, clock.GetUtcNow(), options.AuditRetention, options.AuditMaxEntries) });

    public Task<AuthAuditEntry[]> Read(AuthAuditKind? kind, int take) =>
        Task.FromResult<AuthAuditEntry[]>([.. Snapshot.Entries.Where(entry => kind is null || entry.Kind == kind)
            .OrderByDescending(entry => entry.At).Take(Math.Clamp(take, 1, AuthAuditNeuron.MaxTake))]);
}
