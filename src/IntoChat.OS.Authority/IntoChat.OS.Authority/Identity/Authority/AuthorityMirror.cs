using System.Collections.Concurrent;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Authority.Identity.Grants;

namespace IntoChat.OS.Authority.Identity.Authority;

// Last authority state whose write succeeded in this process. A decision that would consume a Once
// grant is never answered here: that spend stays a single turn on the grain. A miss falls back too.
internal sealed class AuthorityMirror
{
    private readonly ConcurrentDictionary<string, BrainAuthorityState> _states = new(StringComparer.Ordinal);

    public void Publish(string brainScopeId, BrainAuthorityState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentNullException.ThrowIfNull(state);
        _states[brainScopeId] = state;
    }

    public bool TryDecide(CallRequest request, out CallDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(request);
        decision = null;
        var scope = BrainScope.Create(request.Caller.AccountId, request.Caller.BrainId).Id;
        if (!_states.TryGetValue(scope, out var state)) { return false; }
        if (GrantRules.OnceToConsume(request, state.Grants).Count > 0) { return false; }
        decision = BrainAuthorityDecisions.Decide(state, request);
        return true;
    }
}
