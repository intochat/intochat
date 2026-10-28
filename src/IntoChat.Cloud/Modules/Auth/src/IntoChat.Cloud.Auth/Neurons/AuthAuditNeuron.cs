using DigitalBrain.Spi;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Organisations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neuron("auth.audit")]
internal sealed class AuthAuditNeuron(
    AuthModuleOptions options,
    TimeProvider clock) : AuthNeuron<AuthAuditState>, IAuthAuditLog
{
    internal const int MaxTake = 500;

    public async Task<AuthAuditEntry[]> Read(int take)
    {
        var caller = ServiceProvider.GetRequiredService<IAuthHostSeam>().CurrentCaller ?? throw new UnauthorizedAccessException("The audit log is read by a signed-in organisation Admin.");
        var role = await ServiceProvider.GetRequiredService<INeuronProxies>()
            .Get<IOrganisation>(AuthIds.Of(IOrganisation.Type, IOrganisation.Key(OrgBrain()))).RoleOf(caller.PrincipalId);
        if (role != OrganisationRole.Admin)
        {
            throw new UnauthorizedAccessException("Only an organisation Admin reads the audit log.");
        }

        return [.. Snapshot.Entries.OrderByDescending(entry => entry.At).Take(Math.Clamp(take, 1, MaxTake))];
    }

    public Task Append(AuthAuditEntry entry) =>
        SaveQuietly(new AuthAuditState { Entries = AuditPruning.Prune(Snapshot.Entries, entry, clock.GetUtcNow(), options.AuditRetention, options.AuditMaxEntries) });

    private string OrgBrain()
    {
        var key = FullKey;
        return key[..key.LastIndexOf("/auth/audit", StringComparison.Ordinal)];
    }
}

internal static class AuditPruning
{
    public static AuthAuditEntry[] Prune(AuthAuditEntry[] existing, AuthAuditEntry added, DateTimeOffset now, TimeSpan retention, int maxEntries) =>
        [.. existing.Append(added).Where(entry => now - entry.At <= retention).OrderBy(entry => entry.At).TakeLast(maxEntries)];
}
