using System.Security.Cryptography;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

internal sealed class CompiledAppDevelopmentLeases(TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> leases = new(StringComparer.Ordinal);
    internal sealed record Entry(CompiledAppDevelopmentLease Lease, string Account, string Principal, string PackageId,
        AppRequirements Requires, string[] Adapters)
    {
        internal bool Allows(string packageId, AppRequirements? requires, string[] adapters)
            => packageId == PackageId && AppConsent.IsEmpty(AppConsent.Added(Requires, requires)) && !adapters.Except(Adapters, StringComparer.Ordinal).Any();
    }
    internal CompiledAppDevelopmentLease Create(string name, string project, string packageId, AppRequirements requires, string[] adapters, CallerContext owner)
    {
        lock (gate)
        {
            foreach (var expired in leases.Where(x => x.Value.Lease.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) { leases.Remove(expired); }
            if (leases.Count >= 4096) { throw new InvalidOperationException("Too many development leases."); }
            var lease = new CompiledAppDevelopmentLease(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), owner.BrainId, name, project, clock.GetUtcNow().AddMinutes(5));
            leases.Add(lease.Token, new(lease, owner.AccountId, owner.PrincipalId, packageId,
                new(requires.SemanticTypes?.ToArray(), requires.Connectors?.ToArray(), requires.Secrets?.ToArray(), requires.Capabilities?.ToArray()), [.. adapters]));
            return lease;
        }
    }
    internal Entry Require(string token, string project, CallerContext owner)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(token, out var entry) || entry.Lease.ExpiresAt <= clock.GetUtcNow())
            { throw new UnauthorizedAccessException("Development lease expired or was revoked; approve the project again."); }
            if (entry.Lease.Project != project || entry.Lease.Brain != owner.BrainId || entry.Account != owner.AccountId || entry.Principal != owner.PrincipalId)
            { throw new UnauthorizedAccessException("Development lease belongs to another owner, project or brain."); }
            return entry with { Requires = new(entry.Requires.SemanticTypes?.ToArray(), entry.Requires.Connectors?.ToArray(), entry.Requires.Secrets?.ToArray(), entry.Requires.Capabilities?.ToArray()), Adapters = [.. entry.Adapters] };
        }
    }
    internal CompiledAppDevelopmentLease Renew(string token, string project, CallerContext owner)
    {
        lock (gate)
        {
            var entry = Require(token, project, owner);
            var lease = entry.Lease with { ExpiresAt = clock.GetUtcNow().AddMinutes(5) };
            leases[token] = entry with { Lease = lease };
            return lease;
        }
    }
    internal void Revoke(string token, string project, CallerContext owner)
    {
        lock (gate) { _ = Require(token, project, owner); leases.Remove(token); }
    }
}
