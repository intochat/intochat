using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Grants;

namespace DigitalBrain.Platform.Identity.Authority;

// The grain and the same-process mirror both decide through this, so a mirrored allow cannot drift
// from the authority that persists.
internal static class BrainAuthorityDecisions
{
    public static CallDecision? Decide(BrainAuthorityState state, CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.Caller;
        var circleMember = state.Kind == BrainKind.Circle && state.Members.GetValueOrDefault(caller.PrincipalId)?.AccountId == caller.AccountId;
        if (state.AccountId != caller.AccountId && !circleMember || state.BrainId != caller.BrainId)
        { return CallDecision.Deny(CallDenial.OutsideWorkspace, "The target scope does not match this brain."); }
        return GrantRules.Evaluate(request, state.Members.GetValueOrDefault(caller.PrincipalId), state.Grants);
    }
}
