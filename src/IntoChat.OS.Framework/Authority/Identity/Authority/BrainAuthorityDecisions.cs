using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Identity.Authority;

// The grain and the same-process mirror both decide through this, so a mirrored allow cannot drift
// from the authority that persists.
internal static class BrainAuthorityDecisions
{
    // Awaited, never blocked on: the expansion calls neurons, and a grain turn that blocks its scheduler on them deadlocks.
    public static async Task<CallDecision?> Decide(BrainRow state, CallRequest request, IEdgeGrantsCache? edgeCache = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.Caller.ToCapabilityCaller();
        if (caller is null)
        {
            return CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller.");
        }
        var circleMember = state.Kind == BrainKind.Circle && Member(state, caller.PersonId)?.AccountId == caller.AccountId;
        var sameBrainScheduler = caller.Kind == CallerKind.Scheduler && state.BrainId == caller.BrainId;
        if ((!sameBrainScheduler && state.AccountId != caller.AccountId && !circleMember) || state.BrainId != caller.BrainId)
        { return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target scope does not match this brain."); }
        var cache = edgeCache ?? EdgeGrantsCache.Current;
        var principals = await cache.ExpandAsync(GrantsBrainOf(request), caller);
        var connectionBound = caller.Kind == CallerKind.App && request.SemanticTypeIds.Length == 0 && GrantRules.AppOf(caller) is not null
            && await cache.IsConnectionBoundAsync(caller, request.TargetNeuron);
        var grants = state.Grants;
        if (caller.Kind == CallerKind.Agent && !grants.Any(g => g.AppId == CapabilityCallers.AssistantApp))
        {
            grants = [.. grants, .. ShippedGrants.ForAssistant(GrantsBrainOf(request))];
        }
        return GrantRules.Evaluate(request, Member(state, caller.PrincipalId), grants, principals, connectionBound);
    }

    // Grants are owner-declared, so the brain whose grants decide a call is the target's, falling back to the
    // caller's own brain when the target names none.
    internal static string GrantsBrainOf(CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The call filter names the target's brain; a plain neuron's target ({type}/{brain}/{key}) does not start with it.
        if (!string.IsNullOrWhiteSpace(request.TargetScope)) { return request.TargetScope; }
        var callerBrain = request.Caller?.Brain.Value ?? "";
        if (string.IsNullOrWhiteSpace(request.TargetNeuron)) { return callerBrain; }
        return EdgeGrantsCache.TryExtractBrain(request.TargetNeuron, out var brain) ? brain.Value : callerBrain;
    }

    private static Member? Member(BrainRow state, string principalId)
        => state.Members.FirstOrDefault(member => member.PrincipalId == principalId);
}
