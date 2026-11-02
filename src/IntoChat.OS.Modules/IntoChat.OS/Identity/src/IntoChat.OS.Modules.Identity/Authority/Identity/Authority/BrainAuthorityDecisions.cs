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
    public static CallDecision? Decide(BrainRow state, CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.Caller;
        var circleMember = state.Kind == BrainKind.Circle && Member(state, caller.PersonId)?.AccountId == caller.AccountId;
        if (state.AccountId != caller.AccountId && !circleMember || state.BrainId != caller.BrainId)
        { return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target scope does not match this brain."); }
        var principals = EdgeGrantsCache.Current.ExpandAsync(GrantsBrainOf(request), caller).GetAwaiter().GetResult();
        return GrantRules.Evaluate(request, Member(state, caller.PrincipalId), state.Grants, principals);
    }

    // Grants are owner-declared, so the brain whose grants decide a call is the target's, falling back to the
    // caller's own brain when the target names none.
    internal static string GrantsBrainOf(CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var callerBrain = request.Caller?.BrainId ?? "";
        if (string.IsNullOrWhiteSpace(request.TargetNeuron)) { return callerBrain; }
        var brain = EdgeGrantsCache.ExtractBrain(request.TargetNeuron);
        return string.Equals(brain, "local", StringComparison.Ordinal) && callerBrain.Length > 0 ? callerBrain : brain;
    }

    private static Member? Member(BrainRow state, string principalId)
        => state.Members.FirstOrDefault(member => member.PrincipalId == principalId);
}
