using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Authority.Identity.Grants;

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
        return GrantRules.Evaluate(request, Member(state, caller.PrincipalId), state.Grants);
    }

    private static Member? Member(BrainRow state, string principalId)
        => state.Members.FirstOrDefault(member => member.PrincipalId == principalId);
}
