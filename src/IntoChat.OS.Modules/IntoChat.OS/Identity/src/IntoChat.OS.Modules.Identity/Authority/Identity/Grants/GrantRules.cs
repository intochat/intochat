using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Identity.Grants;

// Pure grant rules for the call-filter stage. Kept free of Orleans so the enforcement decision
// can be tested directly: a principal may only act inside the workspace it is a member of, and an
// app caller needs a live grant for every semantic type it touches. A denied value is
// indistinguishable from a missing one.
public static class GrantRules
{
    public static CallDecision? Evaluate(CallRequest request, Member? member, IReadOnlyList<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(request);
        grants ??= [];

        var caller = request.Caller;
        if (caller.Kind is CallerKind.User or CallerKind.Assistant
            && (member is null || member.PrincipalId != caller.PrincipalId || member.AccountId != caller.AccountId || member.BrainId != caller.BrainId))
        {
            return CallDecision.Deny(CallDenial.OutsideWorkspace, "The principal is not a member of this account and brain.");
        }

        if (caller.Kind != CallerKind.App || request.SemanticTypeIds.Length == 0)
        {
            return null;
        }

        if (AppOf(caller) is null)
        {
            return CallDecision.Deny(CallDenial.UntrustedCaller, "An app caller must identify its app, and its principal must be that app's.");
        }

        foreach (var semanticTypeId in request.SemanticTypeIds)
        {
            if (!IsGranted(caller, semanticTypeId, grants))
            {
                return CallDecision.Deny(CallDenial.MissingGrant,
                    $"No grant for semantic type '{semanticTypeId}'. The value is treated as missing.");
            }
        }

        return CallDecision.Allow();
    }

    // The one-time grants a successful app read just spent. An always grant is left in place; only
    // a live Once grant for a requested semantic type is returned, so the store clears exactly those.
    public static IReadOnlyList<Grant> OnceToConsume(CallRequest request, IReadOnlyList<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (grants is null || grants.Count == 0 || request.SemanticTypeIds.Length == 0) { return []; }

        var caller = request.Caller;
        if (caller.Kind != CallerKind.App || AppOf(caller) is not { } app) { return []; }

        return
        [
            .. grants.Where(grant => !grant.Revoked
                && grant.Mode == GrantMode.Once
                && string.Equals(grant.WorkspaceId, caller.BrainId, StringComparison.Ordinal)
                && string.Equals(grant.AppId, app, StringComparison.Ordinal)
                && request.SemanticTypeIds.Contains(grant.SemanticTypeId, StringComparer.Ordinal)
                && !grants.Any(other => other.Mode != GrantMode.Once && !other.Revoked
                    && other.WorkspaceId == grant.WorkspaceId && other.AppId == grant.AppId
                    && other.SemanticTypeId == grant.SemanticTypeId && ModeSatisfied(other, caller))),
        ];
    }

    // The app a caller acts as. The app proxy stamps the app's own principal (AppPrincipal); its app key is the
    // grant subject, which is the AppId grants were always keyed by, so stored grants need no migration. A principal
    // naming another app than AppId is not this app. An app caller still carrying a person principal (stamped before
    // apps had principals) is identified by AppId, as before.
    public static string? AppOf(CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(caller.AppId)) { return null; }
        return AppPrincipal.AppKeyOf(caller.PrincipalId) is { } app && app != caller.AppId ? null : caller.AppId;
    }

    internal static bool IsGranted(CapabilityCaller caller, string semanticTypeId, IReadOnlyList<Grant> grants)
        => AppOf(caller) is { } app && grants.Any(grant => !grant.Revoked
            && string.Equals(grant.WorkspaceId, caller.BrainId, StringComparison.Ordinal)
            && string.Equals(grant.AppId, app, StringComparison.Ordinal)
            && string.Equals(grant.SemanticTypeId, semanticTypeId, StringComparison.Ordinal)
            && ModeSatisfied(grant, caller));

    private static bool ModeSatisfied(Grant grant, CapabilityCaller caller) => grant.Mode switch
    {
        GrantMode.Always or GrantMode.Once => true,
        GrantMode.ThisChat => caller.ConversationId is { Length: > 0 }
            && string.Equals(grant.ConversationId, caller.ConversationId, StringComparison.Ordinal),
        _ => false,
    };
}
