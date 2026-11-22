using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity.Grants;

/// <summary>
/// Adapts <see cref="ICallFilterStage"/> instances (such as <see cref="GrantCallFilterStage"/>) to the runtime-agnostic
/// <see cref="IAccessAdmissionStage"/> seam so grant and zero-token enforcement run identically on TestOS and Silo.
/// </summary>
public sealed class GrantAccessAdmissionStage(
    IEnumerable<ICallFilterStage> stages,
    ICallerAmbient callers) : IAccessAdmissionStage
{
    private readonly ICallFilterStage[] _stages = [.. (stages ?? throw new ArgumentNullException(nameof(stages))).OrderBy(static s => s.Order)];
    private readonly ICallerAmbient _callers = callers ?? throw new ArgumentNullException(nameof(callers));

    public async ValueTask AdmitAsync(NeuronId target, AccessCall call, CancellationToken cancellationToken = default)
    {
        if (_stages.Length == 0)
        {
            return;
        }

        var caller = _callers.Current
            ?? throw new UnauthorizedAccessException($"Unstamped call to '{target}' carried no caller provenance.");

        // Kernel and scheduler hops are trusted host work (activation, offer lookups, signal delivery).
        if (caller.Actor is Actor.Kernel or Actor.Scheduler)
        {
            return;
        }

        var targetNeuron = $"{target.Type}/{target.Brain.Value}/{target.Key}";
        var operation = $"{call.Contract.Name}.{call.Method}";
        var request = new CallRequest
        {
            TargetNeuron = targetNeuron,
            Operation = operation,
            TargetScope = target.Brain.Value,
            EnforceTarget = true,
            PublicOperation = call.Access == OperationAccess.Public,
        };

        foreach (var stage in _stages)
        {
            var decision = await stage.EvaluateAsync(caller, request, cancellationToken).ConfigureAwait(true);
            if (decision is not null)
            {
                if (!decision.Allowed)
                {
                    throw new UnauthorizedAccessException(decision.Explanation ?? $"Call to '{targetNeuron}.{call.Method}' denied: {decision.Denial}");
                }

                return;
            }
        }
    }

    /// <summary>
    /// Creates an <see cref="IAccessAdmissionStage"/> backed by <see cref="GrantCallFilterStage"/> and
    /// <see cref="GrantRules.Evaluate"/> over <paramref name="store"/>, for running the production grant rules on both
    /// TestOS and Silo.
    /// </summary>
    public static IAccessAdmissionStage ForStore(InMemoryGrantStore store, ICallerAmbient callers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(callers);
        return new GrantAccessAdmissionStage([new GrantCallFilterStage(new InMemoryGrantPolicySource(store))], callers);
    }
}

/// <summary>Thread-safe in-memory store of <see cref="Grant"/> records used with <see cref="GrantAccessAdmissionStage.ForStore"/>.</summary>
public sealed class InMemoryGrantStore
{
    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);

    public InMemoryGrantStore(IEnumerable<Grant>? initial = null)
    {
        if (initial is null) { return; }
        foreach (var grant in initial)
        {
            Add(grant);
        }
    }

    public void Add(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _grants[grant.Id] = grant;
    }

    public void Revoke(string grantId)
    {
        if (_grants.TryGetValue(grantId, out var existing))
        {
            _grants[grantId] = existing with { Revoked = true };
        }
    }

    public IReadOnlyList<Grant> Snapshot() => [.. _grants.Values];
}

internal sealed class InMemoryGrantPolicySource(InMemoryGrantStore store) : IGrantPolicySource
{
    public ValueTask<CallDecision?> AuthorizeAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PublicOperation)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        if (caller is null)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.NotAuthenticated, "The request carries no caller."));
        }

        if (caller.Actor is Actor.Kernel or Actor.Scheduler)
        {
            return ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
        }

        if (caller.Actor is Actor.Module)
        {
            return string.IsNullOrEmpty(request.TargetScope)
                || string.Equals(request.TargetScope, caller.Brain.Value, StringComparison.Ordinal)
                ? ValueTask.FromResult<CallDecision?>(CallDecision.Allow())
                : ValueTask.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.OutsideWorkspace, "A module caller may not cross into another brain."));
        }

        Member? member = null;
        if (caller.For is { } person && string.Equals(person.Account, caller.Brain.Value, StringComparison.Ordinal))
        {
            member = new Member
            {
                PrincipalId = person.Principal,
                AccountId = person.Account,
                BrainId = caller.Brain.Value,
                Role = MemberRole.Owner,
                DisplayName = person.Account,
            };
        }

        var decision = GrantRules.Evaluate(
            caller,
            request,
            member,
            store.Snapshot(),
            PrincipalSet.For(caller),
            connectionBound: false);
        return ValueTask.FromResult(decision);
    }
}
