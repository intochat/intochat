using IntoChat.OS.Connections.Identity;

namespace IntoChat.OS.Authority.Identity.Grants;

[DigitalBrain.Neuron(IGrantsNeuron.TypeName)]
public sealed class GrantsNeuron : DigitalBrain.Neuron<GrantsState>, IGrantsNeuron
{
    public Task Grant(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var existing = State.Grants.Where(g => !MatchesGrant(g, grant)).ToList();
        existing.Add(grant);
        var nextVersion = State.Version + 1;
        var nextState = new GrantsState
        {
            Grants = [.. existing],
            Version = nextVersion,
        };
        return Save(nextState, new GrantsChanged(nextVersion));
    }

    public Task Revoke(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId)) { return Task.CompletedTask; }
        var current = State.Grants;
        var filtered = current.Where(g => !string.Equals(g.Id, grantId, StringComparison.Ordinal)
            && !string.Equals(g.Target, grantId, StringComparison.Ordinal)
            && !string.Equals(g.TargetNeuronKey, grantId, StringComparison.Ordinal)).ToArray();
        if (filtered.Length == current.Length)
        {
            return Task.CompletedTask;
        }
        var nextVersion = State.Version + 1;
        var nextState = new GrantsState
        {
            Grants = filtered,
            Version = nextVersion,
        };
        return Save(nextState, new GrantsChanged(nextVersion));
    }

    public Task<Grant[]> List()
    {
        var now = DateTimeOffset.UtcNow;
        var active = State.Grants
            .Where(g => !g.Revoked && (g.Expires is null || g.Expires.Value > now))
            .ToArray();
        return Task.FromResult(active);
    }

    public Task<long> GetVersion() => Task.FromResult(State.Version);

    private static bool MatchesGrant(Grant existing, Grant candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Id) && string.Equals(existing.Id, candidate.Id, StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(candidate.Target) && !string.IsNullOrWhiteSpace(candidate.Grantee))
        {
            return string.Equals(existing.Grantee, candidate.Grantee, StringComparison.Ordinal)
                && string.Equals(existing.Target, candidate.Target, StringComparison.Ordinal);
        }
        return string.Equals(existing.AppId, candidate.AppId, StringComparison.Ordinal)
            && string.Equals(existing.SemanticTypeId, candidate.SemanticTypeId, StringComparison.Ordinal)
            && string.Equals(existing.TargetNeuronKey, candidate.TargetNeuronKey, StringComparison.Ordinal);
    }
}
