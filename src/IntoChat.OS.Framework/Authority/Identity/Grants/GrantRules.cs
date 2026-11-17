using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

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
        "IOsVault",
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
    public static CallDecision? EvaluateZeroTokenGuard(Caller? caller, CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (caller is null) { return null; }
        return EvaluateZeroTokenGuard(CallerIdentity.From(caller), request.TargetNeuron, request.Operation);
    }

    internal static CallDecision? EvaluateZeroTokenGuard(CallerIdentity caller, string? targetNeuron, string? operationText)
    {
        if (caller.IsModule || (caller.Kind != CallerKind.App && !AppPrincipal.IsApp(caller.PrincipalId)))
        {
            return null;
        }

        var target = targetNeuron ?? "";
        var operation = operationText ?? "";
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
    public static CallDecision? Evaluate(Caller? caller, CallRequest request, Member? member, IReadOnlyList<Grant> grants, PrincipalSet principals, bool? connectionBound = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (caller is null)
        {
            return CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller.");
        }
        return Evaluate(
            CallerIdentity.From(caller),
            request.TargetNeuron,
            request.Operation,
            request.SemanticTypeIds,
            request.TargetScope,
            member,
            grants,
            principals,
            connectionBound);
    }

    internal static CallDecision? Evaluate(
        CallerIdentity caller,
        string? targetNeuron,
        string? operation,
        IReadOnlyList<string> semanticTypeIds,
        string? targetScope,
        Member? member,
        IReadOnlyList<Grant> grants,
        PrincipalSet principals,
        bool? connectionBound = null)
    {
        ArgumentNullException.ThrowIfNull(principals);
        grants ??= [];

        if (EvaluateZeroTokenGuard(caller, targetNeuron, operation) is { } zeroTokenDenial)
        {
            return zeroTokenDenial;
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
        if (!isAgent && semanticTypeIds.Count == 0 && (connectionBound ?? EdgeGrantsCache.Current.IsConnectionBound(caller, targetNeuron)))
        {
            return CallDecision.Allow();
        }

        var target = targetNeuron ?? "";
        if (target.StartsWith("neuron://", StringComparison.Ordinal))
        {
            target = target["neuron://".Length..];
        }
        var app = AppOf(caller)!;
        var appKey = BrainId.IsValid(caller.BrainId)
            ? IntoChat.OS.Primitives.AppRef.TryParse(new BrainId(caller.BrainId), app, out var parsed)
                ? parsed.Key
                : IntoChat.OS.Primitives.AppRef.TryOf(new BrainId(caller.BrainId), app, out var named) ? named.Key : app
            : app;

        bool IsOwn(string path) => string.Equals(path, appKey, StringComparison.Ordinal)
            || path.StartsWith(appKey + "/", StringComparison.Ordinal)
            || string.Equals(path, app, StringComparison.Ordinal)
            || path.StartsWith(app + "/", StringComparison.Ordinal);

        // A plain neuron's target is {type}/{brain}/{key}; the app owns it when {brain}/{key} lies under the app.
        var typeEnd = target.IndexOf('/', StringComparison.Ordinal);
        // The csharp runtime gives each app program its own signal session, runtime.session/{brain}/csharp/{app key}.
        var ownSession = string.Equals(target, $"runtime.session/{caller.BrainId}/csharp/{appKey}", StringComparison.Ordinal);
        var ownNotificationEmit = string.Equals(target, $"notifications.inbox/{caller.BrainId}/inbox", StringComparison.Ordinal)
            && IsNotificationEmitOperation(operation);
        if (!isAgent && (ownSession || ownNotificationEmit || IsOwn(target) || typeEnd > 0 && IsOwn(target[(typeEnd + 1)..])))
        {
            return CallDecision.Allow();
        }

        if (semanticTypeIds.Count == 0)
        {
            if (IsGranted(GrantRelations.Run, targetNeuron ?? "", grants, principals, caller, targetScope))
            {
                return CallDecision.Allow();
            }
            return CallDecision.Deny(CallDenial.MissingGrant, $"Caller '{caller.AppId}' requires a valid grant or binding to access '{targetNeuron}'.");
        }

        foreach (var semanticTypeId in semanticTypeIds)
        {
            if (!IsGranted(semanticTypeId, targetNeuron ?? "", grants, principals, caller, targetScope))
            {
                return CallDecision.Deny(CallDenial.MissingGrant, $"No grant for semantic type '{semanticTypeId}'. The value is treated as missing.");
            }
        }

        return CallDecision.Allow();
    }

    // The one-time grants a successful app read just spent. An always grant is left in place; only
    // a live Once grant for a requested semantic type is returned, so the store clears exactly those.
    public static IReadOnlyList<Grant> OnceToConsume(Caller? caller, CallRequest request, IReadOnlyList<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (caller is null) { return []; }
        return OnceToConsume(CallerIdentity.From(caller), request.TargetNeuron, request.SemanticTypeIds, grants);
    }

    internal static IReadOnlyList<Grant> OnceToConsume(CallerIdentity caller, string targetNeuron, IReadOnlyList<string> semanticTypeIds, IReadOnlyList<Grant> grants)
    {
        if (grants is null || grants.Count == 0 || semanticTypeIds.Count == 0) { return []; }
        if (caller.Kind is not (CallerKind.App or CallerKind.Agent) || AppOf(caller) is not { } app) { return []; }

        return
        [
            .. grants.Where(grant => !grant.Revoked
                && grant.Mode == GrantMode.Once
                && string.Equals(grant.WorkspaceId, caller.BrainId, StringComparison.Ordinal)
                && string.Equals(grant.AppId, app, StringComparison.Ordinal)
                && semanticTypeIds.Contains(grant.SemanticTypeId, StringComparer.Ordinal)
                && (grant.TargetNeuronKey is null || string.Equals(grant.TargetNeuronKey, targetNeuron, StringComparison.Ordinal))
                && !grants.Any(other => other.Mode != GrantMode.Once && !other.Revoked
                    && other.WorkspaceId == grant.WorkspaceId && other.AppId == grant.AppId
                    && other.SemanticTypeId == grant.SemanticTypeId
                    && (other.TargetNeuronKey is null || string.Equals(other.TargetNeuronKey, targetNeuron, StringComparison.Ordinal))
                    && ModeSatisfied(other, caller.ConversationId))),
        ];
    }

    // The app a caller acts as. The app proxy stamps the app's own principal (AppPrincipal); its app key is the
    // grant subject, which is the AppId grants were always keyed by, so stored grants need no migration. A principal
    // naming another app than AppId is not this app. An app caller still carrying a person principal (stamped before
    // apps had principals) is identified by AppId, as before.
    public static string? AppOf(CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return AppOf(caller.PrincipalId, caller.AppId);
    }

    internal static string? AppOf(CallerIdentity caller)
        => AppOf(caller.PrincipalId, caller.AppId);

    private static string? AppOf(string principalId, string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) { return null; }
        return AppPrincipal.AppKeyOf(principalId) is { } app && app != appId ? null : appId;
    }

    // targetBrain: the brain the call filter named for the target; a plain neuron's target ({type}/{brain}/{key}) does not start with it.
    internal static bool IsGranted(string relation, string target, IReadOnlyList<Grant> grants, PrincipalSet principals, CapabilityCaller? caller = null,
        string? targetBrain = null)
        => IsGranted(relation, target, grants, principals, caller?.ConversationId, targetBrain);

    internal static bool IsGranted(string relation, string target, IReadOnlyList<Grant> grants, PrincipalSet principals, CallerIdentity caller,
        string? targetBrain = null)
        => IsGranted(relation, target, grants, principals, caller.ConversationId, targetBrain);

    private static bool IsGranted(string relation, string target, IReadOnlyList<Grant> grants, PrincipalSet principals, string? conversationId,
        string? targetBrain)
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

            if (!ModeSatisfied(grant, conversationId))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool ModeSatisfied(Grant grant, string? conversationId) => grant.Mode switch
    {
        GrantMode.Always or GrantMode.Once => true,
        GrantMode.ThisChat => conversationId is { Length: > 0 }
            && string.Equals(grant.ConversationId, conversationId, StringComparison.Ordinal),
        _ => false,
    };

    private static bool IsNotificationEmitOperation(string? operation)
        => !string.IsNullOrWhiteSpace(operation)
            && (string.Equals(operation, "SendAsync", StringComparison.Ordinal)
                || string.Equals(operation, "NotifyAsync", StringComparison.Ordinal)
                || operation.EndsWith(".SendAsync", StringComparison.Ordinal)
                || operation.EndsWith(".NotifyAsync", StringComparison.Ordinal));
}
