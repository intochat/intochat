using System.Collections.Concurrent;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Directory;

namespace IntoChat.OS.Modules.Identity.Authority;

// Last authority state whose write succeeded in this process. A decision that would consume a Once
// grant is never answered here: that spend stays a single turn on the grain. A miss falls back too.
internal sealed class AuthorityMirror(IEdgeGrantsCache? edgeCache = null)
{
    private readonly ConcurrentDictionary<string, IdentityBrainRow> _states = new(StringComparer.Ordinal);

    public void Publish(string brainScopeId, IdentityBrainRow state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentNullException.ThrowIfNull(state);
        _states[brainScopeId] = state;
    }

    public void Remove(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return; }
        _states.TryRemove(brainScopeId, out _);
    }

    public async Task<(bool Decided, CallDecision? Decision)> TryDecide(Caller? caller, CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (caller is null) { return (false, null); }
        var brainId = caller.Brain.Value;
        if (string.IsNullOrWhiteSpace(brainId)) { return (false, null); }
        if (!_states.TryGetValue(brainId, out var state))
        {
            var accountId = caller.For?.Account;
            if (string.IsNullOrWhiteSpace(accountId)) { return (false, null); }
            var scope = BrainScope.Create(accountId, brainId).Id;
            if (!_states.TryGetValue(scope, out state)) { return (false, null); }
        }
        if (GrantRules.OnceToConsume(caller, request, state.Grants).Count > 0) { return (false, null); }
        return (true, await BrainAuthorityDecisions.Decide(state, caller, request, edgeCache));
    }
}
