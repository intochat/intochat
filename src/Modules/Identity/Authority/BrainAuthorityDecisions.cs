using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Identity.Directory;

namespace IntoChat.OS.Modules.Identity.Authority;

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
            caller,
            request.TargetNeuron,
            request.Operation,
            request.SemanticTypeIds,
            request.TargetScope,
            edgeCache);
    }

    internal static async Task<CallDecision?> Decide(
        IdentityBrainRow state,
        Caller caller,
        string targetNeuron,
        string operation,
        IReadOnlyList<string> semanticTypeIds,
        string? targetScope,
        IEdgeGrantsCache? edgeCache = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(caller);
        var brain = caller.Brain.Value;
        var person = caller.For;
        var circleMember = state.Kind == BrainKind.Circle && person is not null && Member(state, person.Principal)?.AccountId == person.Account;
        var sameBrainSystem = caller.Actor is Actor.Scheduler or Actor.Module && state.BrainId == brain;
        if ((!sameBrainSystem && (person is null || state.AccountId != person.Account) && !circleMember) || state.BrainId != brain)
        { return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target scope does not match this brain."); }
        var grantsBrain = GrantsBrainOf(brain, targetNeuron, targetScope);
        var cache = edgeCache ?? EdgeGrantsCache.Current;
        var principals = await cache.ExpandAsync(grantsBrain, caller);
        var connectionBound = caller.Actor is Actor.App && semanticTypeIds.Count == 0
            && await cache.IsConnectionBoundAsync(caller, targetNeuron);
        var grants = ShippedGrants.WithImplicit(state.Grants, caller, grantsBrain);
        return GrantRules.Evaluate(caller, targetNeuron, operation, semanticTypeIds, targetScope,
            person is null ? null : Member(state, person.Principal), grants, principals, connectionBound);
    }

    // The directory's decision for a call, made from the caller value over the directory's authority state. A one-time grant
    // the decision consumes is spent in one directory turn; when another call spent it first, this call is refused.
    public static async Task<CallDecision?> Authorize(Caller? caller, CallRequest request,
        Func<string, string, Task<IdentityBrainRow>> authorityOf, Func<string, string, string[], Task<bool>> spendOnce, IEdgeGrantsCache? edgeCache = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorityOf);
        ArgumentNullException.ThrowIfNull(spendOnce);
        if (caller is null) { return CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller."); }
        var grantsBrain = GrantsBrainOf(caller, request);
        var state = await authorityOf(caller.Brain.Value, grantsBrain);
        var decision = await Decide(state, caller, request, edgeCache);
        if (decision is not { Allowed: true }) { return decision; }
        var consumed = GrantRules.OnceToConsume(caller, request, state.Grants);
        if (consumed.Count == 0) { return decision; }
        return await spendOnce(caller.Brain.Value, grantsBrain, [.. consumed.Select(grant => grant.Id)])
            ? decision with { ConsumesOnce = true }
            : CallDecision.Deny(CallDenial.MissingGrant, "The one-time grant was already used.");
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
