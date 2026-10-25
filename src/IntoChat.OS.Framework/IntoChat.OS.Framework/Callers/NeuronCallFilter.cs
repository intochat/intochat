using System.Collections.Concurrent;
using System.Reflection;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Framework;

internal sealed class NeuronCallFilter(ICallFilter filter, ILogger<NeuronCallFilter> logger) : IIncomingGrainCallFilter
{
    private const string ModuleCall = CallerContextStamper.ModuleCallKey;
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // Policy covers INeuron contracts only, and is skipped for: non-neuron grains, unstamped calls
        // (startup, reminders: public edges must stamp a caller), and a neuron's own non-INeuron grain
        // interfaces. Every contract an edge resolves derives INeuron (CompiledTypesFacts), so those
        // interfaces are cluster plumbing no edge can address.
        if (context.Grain is not Neuron neuron || !CallerContextStamper.TryGet(out var caller)
            || IsInbound(neuron, context.InterfaceMethod.DeclaringType!))
        { await context.Invoke(); return; }

        // A nudge is delivered by its publishing grain, not by the user whose operation happened to
        // produce it. Only that verified delivery boundary enters internal context; ordinary calls and
        // fabricated callbacks keep policy checks.
        if (context.InterfaceMethod.DeclaringType == typeof(INeuronObserver)
            && context.InterfaceMethod.Name == nameof(INeuronObserver.Nudge)
            && context.SourceId is { } publisher
            && !publisher.Type.ToString().StartsWith("sys.client", StringComparison.Ordinal)
            && context.Request.GetArgument(0) is SignalNudge nudge && nudge.Publisher.Name == publisher.ToString())
        {
            Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
            try { await context.Invoke(); }
            finally { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, caller); }
            return;
        }

        var (scope, isPublic) = Policy(neuron, context.InterfaceMethod) switch
        {
            PublicAttribute => (null, true),
            InternalAttribute => (null, false),
            SharedAttribute => (BrainScope.CurrentId(), false),
            PerAccountAttribute => (caller.AccountId == neuron.GetPrimaryKeyString()
                ? BrainScope.Create(caller.AccountId, caller.BrainId).Id : null, false),
            // A neuron keyed by the brain id itself (workspace, kernel) is in that brain.
            _ => (BrainKey.ScopeOf(neuron.GetPrimaryKeyString())
                ?? (neuron.GetPrimaryKeyString() == caller.BrainId ? caller.BrainId : null), false),
        };
        var unclassified = scope is null && !isPublic;
        // Module services can call through an injected client after leaving the grain
        // scheduler. Preserve that provenance without changing the end-user identity.
        var internalCall = Orleans.Runtime.RequestContext.Get(ModuleCall) is true
            || context.SourceId is { } source && !source.Type.ToString().StartsWith("sys.client", StringComparison.Ordinal);
        if (unclassified && internalCall)
        { await context.Invoke(); return; }

        var decision = await filter.AuthorizeAsync(new()
        {
            Caller = caller,
            TargetNeuron = neuron.GetGrainId().ToString(),
            Operation = context.InterfaceMethod.Name,
            EnforceTarget = true,
            TargetScope = scope,
            PublicOperation = isPublic,
        });
        // The target's type and operation (never its key) turn a denial from a riddle into a
        // pointer at the neuron whose classification needs a decision.
        if (!decision.Allowed)
        {
            logger.LogWarning("Access denied: {Principal} in brain {BrainId} calling {Operation} on {Target}: {Explanation}",
                caller.PrincipalId, caller.BrainId, context.InterfaceMethod.Name, neuron.GetGrainId(), decision.Explanation);
            throw new UnauthorizedAccessException(
                $"{decision.Explanation} (target {neuron.GetGrainId().Type}, operation {context.InterfaceMethod.Name})");
        }
        Orleans.Runtime.RequestContext.Set(ModuleCall, true);
        await context.Invoke();
    }

    // Null is the owner rule.
    private static readonly ConcurrentDictionary<(Type Grain, MethodInfo Method), AccessAttribute?> Policies = new();

    // The method's attribute wins over its contract's. Watch/Unwatch are declared on INeuron, so
    // they take the policy the neuron's own contracts declare, falling back to the owner rule.
    private static AccessAttribute? Policy(Neuron neuron, MethodInfo method)
        => Policies.GetOrAdd((neuron.GetType(), method), static key =>
        {
            if (key.Method.GetCustomAttribute<AccessAttribute>() is { } declared) { return declared; }
            if (key.Method.DeclaringType != typeof(INeuron))
            { return key.Method.DeclaringType!.GetCustomAttribute<AccessAttribute>(); }
            var policies = key.Grain.GetInterfaces()
                .Where(type => type != typeof(INeuron) && typeof(INeuron).IsAssignableFrom(type))
                .Select(type => type.GetCustomAttribute<AccessAttribute>())
                .OfType<AccessAttribute>().DistinctBy(attribute => attribute.GetType()).ToArray();
            return policies.Length == 1 ? policies[0] : null;
        });

    // Signal delivery and Orleans extensions (streamed results) stay neuron surface.
    private static bool IsInbound(Neuron neuron, Type contract)
        => !typeof(INeuron).IsAssignableFrom(contract) && contract != typeof(INeuronObserver) && contract.IsInstanceOfType(neuron);
}
