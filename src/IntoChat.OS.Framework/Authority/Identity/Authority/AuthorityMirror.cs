using System.Collections.Concurrent;
using DigitalBrain.Brains;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Identity.Authority;

// Last authority state whose write succeeded in this process. A decision that would consume a Once
// grant is never answered here: that spend stays a single turn on the grain. A miss falls back too.
internal sealed class AuthorityMirror
{
    private readonly ConcurrentDictionary<string, BrainRow> _states = new(StringComparer.Ordinal);

    public void Publish(string brainScopeId, BrainRow state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainScopeId);
        ArgumentNullException.ThrowIfNull(state);
        _states[brainScopeId] = state;
    }

    public async Task<(bool Decided, CallDecision? Decision)> TryDecide(CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brainId = request.Caller.Brain.Value;
        if (string.IsNullOrWhiteSpace(brainId)) { return (false, null); }
        if (!_states.TryGetValue(brainId, out var state))
        {
            var accountId = request.Caller.For?.Account;
            if (string.IsNullOrWhiteSpace(accountId)) { return (false, null); }
            var scope = BrainScope.Create(accountId, brainId).Id;
            if (!_states.TryGetValue(scope, out state)) { return (false, null); }
        }
        if (GrantRules.OnceToConsume(request, state.Grants).Count > 0) { return (false, null); }
        return (true, await BrainAuthorityDecisions.Decide(state, request));
    }
}
