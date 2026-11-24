using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity.Grants;

// Pure grant rules for the call-filter stage. Kept free of Orleans so the enforcement decision
// can be tested directly: a principal may only act inside the workspace it is a member of, and an
// app caller needs a live grant for every semantic type it touches. A denied value is
// indistinguishable from a missing one.
public static class GrantRules
{
    private static readonly HashSet<string> ForbiddenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "identity.vault",
        "identity.brain-keys",
        "vault",
        "secrets",
        "oauth-exchange",
    };

    private static readonly HashSet<string> ForbiddenInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "IOsVault",
        "IBrainKeys",
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
        return EvaluateZeroTokenGuard(caller, request.TargetNeuron, request.Operation);
    }

    // An app, or a person stamped with an app's principal (refused at admission, held to the app's limits here too).
    internal static bool IsAppCaller(Caller caller)
        => caller.Actor is Actor.App || (caller.Actor is Actor.Person person && AppPrincipal.IsApp(person.Principal));

    internal static CallDecision? EvaluateZeroTokenGuard(Caller caller, string? targetNeuron, string? operationText)
    {
        if (!IsAppCaller(caller))
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
            caller,
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
        Caller caller,
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

        // A person, or an agent for its person, acts only inside a brain that person is a member of.
        if (caller.Actor is Actor.Person or Actor.Agent
            && (member is null || caller.For is not { } person || member.PrincipalId != person.Principal || member.AccountId != person.Account
                || member.BrainId != caller.Brain.Value))
        {
            return CallDecision.Deny(CallDenial.OutsideWorkspace, "The principal is not a member of this account and brain.");
        }

        if (caller.Actor is not (Actor.App or Actor.Agent))
        {
            return CallDecision.Allow();
        }

        // An agent reaches everything through grants only: no connection-bound shortcut, no own-target allowance.
        var isAgent = caller.Actor is Actor.Agent;
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
        var appKey = IntoChat.OS.Host.Primitives.AppRef.TryParse(caller.Brain, app, out var parsed)
            ? parsed.Key
            : IntoChat.OS.Host.Primitives.AppRef.TryOf(caller.Brain, app, out var named) ? named.Key : app;

        bool IsOwn(string path) => string.Equals(path, appKey, StringComparison.Ordinal)
            || path.StartsWith(appKey + "/", StringComparison.Ordinal)
            || string.Equals(path, app, StringComparison.Ordinal)
            || path.StartsWith(app + "/", StringComparison.Ordinal);

        // A plain neuron's target is {type}/{brain}/{key}; the app owns it when {brain}/{key} lies under the app.
        var typeEnd = target.IndexOf('/', StringComparison.Ordinal);
        // The csharp runtime gives each app program its own signal session, runtime.session/{brain}/csharp/{app key}.
        var ownSession = string.Equals(target, $"runtime.session/{caller.Brain.Value}/csharp/{appKey}", StringComparison.Ordinal);
        var ownNotificationEmit = string.Equals(target, $"notifications.inbox/{caller.Brain.Value}/inbox", StringComparison.Ordinal)
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
            return CallDecision.Deny(CallDenial.MissingGrant, $"Caller '{app}' requires a valid grant or binding to access '{targetNeuron}'.");
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
        return OnceToConsume(caller, request.TargetNeuron, request.SemanticTypeIds, grants);
    }

    internal static IReadOnlyList<Grant> OnceToConsume(Caller caller, string targetNeuron, IReadOnlyList<string> semanticTypeIds, IReadOnlyList<Grant> grants)
    {
        if (grants is null || grants.Count == 0 || semanticTypeIds.Count == 0) { return []; }
        if (AppOf(caller) is not { } app) { return []; }

        return
        [
            .. grants.Where(grant => !grant.Revoked
                && grant.Mode == GrantMode.Once
                && string.Equals(grant.WorkspaceId, caller.Brain.Value, StringComparison.Ordinal)
                && string.Equals(grant.AppId, app, StringComparison.Ordinal)
                && semanticTypeIds.Contains(grant.SemanticTypeId, StringComparer.Ordinal)
                && (grant.TargetNeuronKey is null || string.Equals(grant.TargetNeuronKey, targetNeuron, StringComparison.Ordinal))
                && !grants.Any(other => other.Mode != GrantMode.Once && !other.Revoked
                    && other.WorkspaceId == grant.WorkspaceId && other.AppId == grant.AppId
                    && other.SemanticTypeId == grant.SemanticTypeId
                    && (other.TargetNeuronKey is null || string.Equals(other.TargetNeuronKey, targetNeuron, StringComparison.Ordinal))
                    && ModeSatisfied(other, caller.Conversation))),
        ];
    }

    // The app a caller acts as, which is the subject its grants are recorded for: an app by its key, an agent as the
    // assistant. Every other actor has none.
    public static string? AppOf(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.Actor switch
        {
            Actor.App app => app.Key,
            Actor.Agent => CallerApps.Assistant,
            _ => null,
        };
    }

    // targetBrain: the brain the call filter named for the target; a plain neuron's target ({type}/{brain}/{key}) does not start with it.
    internal static bool IsGranted(string relation, string target, IReadOnlyList<Grant> grants, PrincipalSet principals, Caller? caller = null,
        string? targetBrain = null)
        => IsGranted(relation, target, grants, principals, caller?.Conversation, targetBrain);

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
