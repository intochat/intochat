using IntoChat.OS.Connections.Identity;
using GrantModel = IntoChat.OS.Connections.Identity.Grant;

namespace IntoChat.OS.Authority.Identity.Grants;

[DigitalBrain.Neuron(IGrantsNeuron.TypeName)]
public sealed class GrantsNeuron : DigitalBrain.Neuron<GrantsState>, IGrantsNeuron
{
    private async Task<GrantsState> Normalized()
    {
        var grants = State.Grants;
        var needsNormalization = false;
        for (var i = 0; i < grants.Length; i++)
        {
            var g = grants[i];
            if (!g.IsCanonical || g != GrantModel.Normalize(g))
            {
                needsNormalization = true;
                break;
            }
        }

        if (!needsNormalization)
        {
            return State;
        }

        var nextGrants = grants.Select(GrantModel.Normalize).ToArray();
        var nextVersion = State.Version + 1;
        var nextState = new GrantsState
        {
            Grants = nextGrants,
            Version = nextVersion,
        };
        await Save(nextState, new GrantsChanged(nextVersion));
        return nextState;
    }

    public async Task Grant(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var state = await Normalized();
        var existing = state.Grants.Where(g => !MatchesGrant(g, grant)).ToList();
        existing.Add(grant);
        var nextVersion = state.Version + 1;
        var nextState = new GrantsState
        {
            Grants = [.. existing],
            Version = nextVersion,
        };
        await Save(nextState, new GrantsChanged(nextVersion));
    }

    public async Task Revoke(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId)) { return; }
        var state = await Normalized();
        var current = state.Grants;
        var filtered = current.Where(g => !string.Equals(g.Id, grantId, StringComparison.Ordinal)).ToArray();
        if (filtered.Length == current.Length)
        {
            return;
        }
        var nextVersion = state.Version + 1;
        var nextState = new GrantsState
        {
            Grants = filtered,
            Version = nextVersion,
        };
        await Save(nextState, new GrantsChanged(nextVersion));
    }

    public async Task<Grant[]> List()
    {
        var state = await Normalized();
        var now = DateTimeOffset.UtcNow;
        var active = state.Grants
            .Where(g => !g.Revoked && (g.Expires is null || g.Expires.Value > now))
            .ToArray();
        return active;
    }

    public Task<long> GetVersion() => Task.FromResult(State.Version);

    private static bool MatchesGrant(Grant existing, Grant candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Id) && string.Equals(existing.Id, candidate.Id, StringComparison.Ordinal))
        {
            return true;
        }
        var eNorm = GrantModel.Normalize(existing);
        var cNorm = GrantModel.Normalize(candidate);
        return string.Equals(eNorm.Grantee, cNorm.Grantee, StringComparison.Ordinal)
            && string.Equals(eNorm.Target, cNorm.Target, StringComparison.Ordinal)
            && string.Equals(eNorm.Scope, cNorm.Scope, StringComparison.Ordinal);
    }
}
