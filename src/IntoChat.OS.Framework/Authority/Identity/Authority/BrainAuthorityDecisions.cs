using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Identity.Authority;

// The grain and the same-process mirror both decide through this, so a mirrored allow cannot drift
// from the authority that persists.
internal static class BrainAuthorityDecisions
{
    // Awaited, never blocked on: the expansion calls neurons, and a grain turn that blocks its scheduler on them deadlocks.
    public static Task<CallDecision?> Decide(IdentityBrainRow state, Caller? caller, CallRequest request, IEdgeGrantsCache? edgeCache = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        if (caller is null)
        {
            return Task.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller."));
        }
        return Decide(
            state,
            CallerIdentity.From(caller),
            request.TargetNeuron,
            request.Operation,
            request.SemanticTypeIds,
            request.TargetScope,
            edgeCache);
    }

    internal static async Task<CallDecision?> Decide(
        IdentityBrainRow state,
        CallerIdentity caller,
        string targetNeuron,
        string operation,
        IReadOnlyList<string> semanticTypeIds,
        string? targetScope,
        IEdgeGrantsCache? edgeCache = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var circleMember = state.Kind == BrainKind.Circle && Member(state, caller.PersonId)?.AccountId == caller.AccountId;
        var sameBrainSystem = (caller.Kind == CallerKind.Scheduler || caller.IsModule) && state.BrainId == caller.BrainId;
        if ((!sameBrainSystem && state.AccountId != caller.AccountId && !circleMember) || state.BrainId != caller.BrainId)
        { return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target scope does not match this brain."); }
        var grantsBrain = GrantsBrainOf(caller.BrainId, targetNeuron, targetScope);
        var cache = edgeCache ?? EdgeGrantsCache.Current;
        var capCaller = caller.ToCapabilityCaller();
        var principals = cache is EdgeGrantsCache concrete
            ? await concrete.ExpandAsync(grantsBrain, caller)
            : await cache.ExpandAsync(grantsBrain, capCaller);
        var connectionBound = caller.Kind == CallerKind.App && semanticTypeIds.Count == 0 && GrantRules.AppOf(caller) is not null
            && (cache is EdgeGrantsCache directCache
                ? await directCache.IsConnectionBoundAsync(caller, targetNeuron)
                : await cache.IsConnectionBoundAsync(capCaller, targetNeuron));
        var grants = ShippedGrants.WithImplicit(state.Grants, caller.Kind, caller.AppId, grantsBrain);
        return GrantRules.Evaluate(caller, targetNeuron, operation, semanticTypeIds, targetScope, Member(state, caller.PrincipalId), grants, principals, connectionBound);
    }

    // Grants are owner-declared, so the brain whose grants decide a call is the target's, falling back to the
    // caller's own brain when the target names none.
    internal static string GrantsBrainOf(Caller? caller, CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GrantsBrainOf(caller?.Brain.Value ?? "", request.TargetNeuron, request.TargetScope);
    }

    internal static string GrantsBrainOf(string callerBrain, string targetNeuron, string? targetScope)
    {
        // The call filter names the target's brain; a plain neuron's target ({type}/{brain}/{key}) does not start with it.
        if (!string.IsNullOrWhiteSpace(targetScope)) { return targetScope; }
        if (string.IsNullOrWhiteSpace(targetNeuron)) { return callerBrain; }
        return EdgeGrantsCache.TryExtractBrain(targetNeuron, out var brain) ? brain.Value : callerBrain;
    }

    private static Member? Member(IdentityBrainRow state, string principalId)
        => state.Members.FirstOrDefault(member => member.PrincipalId == principalId);
}
