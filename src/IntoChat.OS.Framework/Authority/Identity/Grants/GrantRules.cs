using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Sealing;
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
    private static readonly HashSet<string> ForbiddenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "identity.vault",
        "vault",
        "secrets",
        "oauth-exchange",
    };

    private static readonly HashSet<string> ForbiddenInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "IVault",
        "ISecretValueSource",
        "VaultSecretValueSource",
        "IBrainSecretSealer",
        "BrainSecretSealer",
        "ISecretSealer",
        "SecretSealer",
        "OAuthTokenExchange",
        "IOAuthTokenExchange",
    };

    private static readonly HashSet<string> ForbiddenMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExportFor",
        "AcceptGrant",
        "OpenSecret",
        "CurrentValues",
    };

    // Zero-token auth enforcement: sandbox and app callers can never read raw OAuth credentials or unsealed secrets directly.
    public static CallDecision? EvaluateZeroTokenGuard(CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = request.Caller.ToCapabilityCaller();
        if (caller is null) { return null; }
        if (caller.Kind != CallerKind.App && !AppPrincipal.IsApp(caller.PrincipalId))
        {
            return null;
        }

        var target = request.TargetNeuron ?? "";
        var operation = request.Operation ?? "";
        if (IsForbiddenTarget(target))
        {
            return CallDecision.Deny(
                CallDenial.UntrustedCaller,
                "Zero-token auth enforcement: sandbox and app callers cannot access raw credentials or unsealed secrets directly; use ICapabilityBroker and ConnectionNeuron.UseCredentialAsync.");
        }

        if (IsForbiddenOperation(operation))
        {
            return CallDecision.Deny(
                CallDenial.UntrustedCaller,
                "Zero-token auth enforcement: sandbox and app callers cannot invoke raw secret or credential operations directly; use ICapabilityBroker and ConnectionNeuron.UseCredentialAsync.");
        }

        return null;
    }

    private static bool IsForbiddenTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) { return false; }
        if (ForbiddenTypes.Contains(target) || ForbiddenInterfaces.Contains(target)) { return true; }
        var segments = target.Split('/');
        foreach (var segment in segments)
        {
            if (ForbiddenTypes.Contains(segment) || ForbiddenInterfaces.Contains(segment)) { return true; }
        }
        return false;
    }

    private static bool IsForbiddenOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)) { return false; }
        if (ForbiddenMethods.Contains(operation) || ForbiddenInterfaces.Contains(operation)) { return true; }
        var dotSegments = operation.Split('.');
        foreach (var segment in dotSegments)
        {
            if (ForbiddenInterfaces.Contains(segment) || ForbiddenMethods.Contains(segment)) { return true; }
        }
        return false;
    }

    // connectionBound: the caller's awaited answer; without one the cache is asked synchronously, which a grain turn must not do.
    public static CallDecision? Evaluate(CallRequest request, Member? member, IReadOnlyList<Grant> grants, PrincipalSet principals, bool? connectionBound = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principals);
        grants ??= [];

        if (EvaluateZeroTokenGuard(request) is { } zeroTokenDenial)
        {
            return zeroTokenDenial;
        }

        var caller = request.Caller.ToCapabilityCaller();
        if (caller is null)
        {
            return CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller.");
        }

        if (caller.Kind is CallerKind.User or CallerKind.Agent
            && (member is null || member.PrincipalId != caller.PrincipalId || member.AccountId != caller.AccountId || member.BrainId != caller.BrainId))
        {
            return CallDecision.Deny(CallDenial.OutsideWorkspace, "The principal is not a member of this account and brain.");
        }

        if (caller.Kind is not (CallerKind.App or CallerKind.Agent))
        {
            return CallDecision.Allow();
        }

        if (AppOf(caller) is null)
        {
            return CallDecision.Deny(CallDenial.UntrustedCaller, "An app caller must identify its app, and its principal must be that app's.");
        }

        // An agent reaches everything through grants only: no connection-bound shortcut, no own-target allowance.
        var isAgent = caller.Kind == CallerKind.Agent;
        if (!isAgent && request.SemanticTypeIds.Length == 0 && (connectionBound ?? EdgeGrantsCache.Current.IsConnectionBound(caller, request.TargetNeuron)))
        {
            return CallDecision.Allow();
        }

        var target = request.TargetNeuron ?? "";
        if (target.StartsWith("neuron://", StringComparison.Ordinal))
        {
            target = target["neuron://".Length..];
        }
        var app = AppOf(caller)!;
        var ownPrefix1 = $"{caller.BrainId}/apps/{app}";
        var ownPrefix2 = app.StartsWith($"{caller.BrainId}/apps/", StringComparison.Ordinal) ? app : null;
        var ownPrefix3 = app; // if app is already "local/apps/calculator"

        bool IsOwn(string path) => string.Equals(path, ownPrefix1, StringComparison.Ordinal)
            || path.StartsWith(ownPrefix1 + "/", StringComparison.Ordinal)
            || (ownPrefix2 is not null && (string.Equals(path, ownPrefix2, StringComparison.Ordinal) || path.StartsWith(ownPrefix2 + "/", StringComparison.Ordinal)))
            || string.Equals(path, ownPrefix3, StringComparison.Ordinal)
            || path.StartsWith(ownPrefix3 + "/", StringComparison.Ordinal);

        // A plain neuron's target is {type}/{brain}/{key}; the app owns it when {brain}/{key} lies under the app.
        var typeEnd = target.IndexOf('/', StringComparison.Ordinal);
        // The csharp runtime gives each app program its own signal session, runtime.session/{brain}/csharp/{app key}.
        var appKey = ownPrefix2 ?? ownPrefix1;
        var ownSession = string.Equals(target, $"runtime.session/{caller.BrainId}/csharp/{appKey}", StringComparison.Ordinal);
        var ownNotificationEmit = string.Equals(target, $"notifications.inbox/{caller.BrainId}/inbox", StringComparison.Ordinal)
            && IsNotificationEmitOperation(request.Operation);
        if (!isAgent && (ownSession || ownNotificationEmit || IsOwn(target) || typeEnd > 0 && IsOwn(target[(typeEnd + 1)..])))
        {
            return CallDecision.Allow();
        }

        if (request.SemanticTypeIds.Length == 0)
        {
            if (IsGranted(GrantRelations.Run, request.TargetNeuron ?? "", grants, principals, caller, request.TargetScope))
            {
                return CallDecision.Allow();
            }
            return CallDecision.Deny(CallDenial.MissingGrant, $"Caller '{caller.AppId}' requires a valid grant or binding to access '{request.TargetNeuron}'.");
        }

        foreach (var semanticTypeId in request.SemanticTypeIds)
        {
            if (!IsGranted(semanticTypeId, request.TargetNeuron ?? "", grants, principals, caller, request.TargetScope))
            {
                return CallDecision.Deny(CallDenial.MissingGrant, $"No grant for semantic type '{semanticTypeId}'. The value is treated as missing.");
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

        var caller = request.Caller.ToCapabilityCaller();
        if (caller is null || caller.Kind is not (CallerKind.App or CallerKind.Agent) || AppOf(caller) is not { } app) { return []; }

        return
        [
            .. grants.Where(grant => !grant.Revoked
                && grant.Mode == GrantMode.Once
                && string.Equals(grant.WorkspaceId, caller.BrainId, StringComparison.Ordinal)
                && string.Equals(grant.AppId, app, StringComparison.Ordinal)
                && request.SemanticTypeIds.Contains(grant.SemanticTypeId, StringComparer.Ordinal)
                && (grant.TargetNeuronKey is null || string.Equals(grant.TargetNeuronKey, request.TargetNeuron, StringComparison.Ordinal))
                && !grants.Any(other => other.Mode != GrantMode.Once && !other.Revoked
                    && other.WorkspaceId == grant.WorkspaceId && other.AppId == grant.AppId
                    && other.SemanticTypeId == grant.SemanticTypeId
                    && (other.TargetNeuronKey is null || string.Equals(other.TargetNeuronKey, request.TargetNeuron, StringComparison.Ordinal))
                    && ModeSatisfied(other, caller))),
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

    // targetBrain: the brain the call filter named for the target; a plain neuron's target ({type}/{brain}/{key}) does not start with it.
    internal static bool IsGranted(string relation, string target, IReadOnlyList<Grant> grants, PrincipalSet principals, CapabilityCaller? caller = null,
        string? targetBrain = null)
    {
        if (grants is null || grants.Count == 0 || string.IsNullOrWhiteSpace(relation))
        {
            return false;
        }

        var cleanTarget = target.StartsWith("neuron://", StringComparison.Ordinal) ? target["neuron://".Length..] : target;
        var now = DateTimeOffset.UtcNow;

        foreach (var rawGrant in grants)
        {
            if (rawGrant.Revoked)
            {
                continue;
            }

            if (rawGrant.Expires is { } expires && expires <= now)
            {
                continue;
            }

            var grant = Grant.Normalize(rawGrant);

            var scopeMatches = string.Equals(grant.Scope, relation, StringComparison.Ordinal)
                || (string.Equals(grant.Scope, "*", StringComparison.Ordinal) && !GrantRelations.IsUseVia(relation));
            if (!scopeMatches)
            {
                continue;
            }

            if (!principals.Principals.Contains(grant.Grantee, StringComparer.Ordinal))
            {
                continue;
            }

            // Owner-declared: only the target brain's owner can grant access to its neurons.
            var resolvedTargetBrain = !string.IsNullOrWhiteSpace(targetBrain)
                ? targetBrain
                : (EdgeGrantsCache.TryExtractBrain(cleanTarget, out var parsedBrain) ? parsedBrain.Value : string.Empty);
            if (string.IsNullOrEmpty(resolvedTargetBrain) || !string.Equals(grant.Owner, "brain:" + resolvedTargetBrain, StringComparison.Ordinal))
            {
                continue;
            }

            var grantTarget = grant.Target;
            if (grantTarget.StartsWith("neuron://", StringComparison.Ordinal))
            {
                grantTarget = grantTarget["neuron://".Length..];
            }

            bool targetMatches = string.Equals(grantTarget, "*", StringComparison.Ordinal)
                || string.Equals(grantTarget, cleanTarget, StringComparison.Ordinal)
                || (grantTarget.EndsWith("/*", StringComparison.Ordinal) && cleanTarget.StartsWith(grantTarget.TrimEnd('*'), StringComparison.Ordinal));

            if (!targetMatches)
            {
                continue;
            }

            if (caller is not null && !ModeSatisfied(grant, caller))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool ModeSatisfied(Grant grant, CapabilityCaller caller) => grant.Mode switch
    {
        GrantMode.Always or GrantMode.Once => true,
        GrantMode.ThisChat => caller.ConversationId is { Length: > 0 }
            && string.Equals(grant.ConversationId, caller.ConversationId, StringComparison.Ordinal),
        _ => false,
    };

    private static bool IsNotificationEmitOperation(string? operation)
        => !string.IsNullOrWhiteSpace(operation)
            && (string.Equals(operation, "SendAsync", StringComparison.Ordinal)
                || string.Equals(operation, "NotifyAsync", StringComparison.Ordinal)
                || operation.EndsWith(".SendAsync", StringComparison.Ordinal)
                || operation.EndsWith(".NotifyAsync", StringComparison.Ordinal));
}
