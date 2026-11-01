using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-04; until 06-08 moves access to the runtime): the os access policy NeuronCallFilter applies to grain neurons,
// applied to calls into plain neurons on the runtime's host grains. An edge-stamped caller reaches a plain neuron only when the
// policy allows it, exactly as it would reach the grain the neuron replaced: the owner rule (the neuron's brain must be the
// caller's) unless the contract is module-internal or or method declares [Public], [Internal], [Shared] or [PerAccount]. Unstamped calls (silo
// services, timers, runtime plumbing without a caller) pass, as they do for grains.
internal sealed class PlainNeuronCallFilter(ICallFilter filter, ILogger<PlainNeuronCallFilter> logger) : IIncomingGrainCallFilter
{
    private static readonly ConcurrentDictionary<(string Contract, string Method, int Arity), AccessAttribute?> Policies = new();

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is not INeuronHost || context.Request.GetArgumentCount() == 0 || context.Request.GetArgument(0) is not RuntimeInvocation call)
        { await context.Invoke(); return; }

        if (IsPlumbing(call)) { await context.Invoke(); return; }

        if (!CallerContextStamper.TryGet(out var caller) || caller is null)
        {
            logger.LogWarning("Access denied: unstamped call to plain neuron {Contract}.{Method}", call.Contract, call.Method);
            throw new UnauthorizedAccessException($"Unstamped calls are not authorized for plain neuron {call.Contract}.{call.Method}");
        }

        var id = DigitalBrain.NeuronId.Parse(context.TargetId.Key.ToString()!);
        // As for grains (NeuronCallFilter): a module service calling through an injected client after leaving a turn, or any call
        // from inside the cluster, keeps its provenance, so an unclassified ([Internal]) operation passes for it (06-08: the
        // fast-path writer completes a plain app's synapse after the person's request returned).
        var (scope, isPublic) = Classify(caller, id, call);
        var internalCall = Orleans.Runtime.RequestContext.Get(CallerContextStamper.ModuleCallKey) is true
            || context.SourceId is { } source && !source.Type.ToString()!.StartsWith("sys.client", StringComparison.Ordinal);
        if (scope is null && !isPublic && internalCall) { await context.Invoke(); return; }
        await Authorize(filter, logger, caller, id, call);
        Orleans.Runtime.RequestContext.Set(CallerContextStamper.ModuleCallKey, true);
        await context.Invoke();
    }

    // The target scope and publicity the os access policy gives one call of caller into the plain neuron id.
    private static (string? Scope, bool IsPublic) Classify(CapabilityCaller caller, DigitalBrain.NeuronId id, RuntimeInvocation call) => Policy(call) switch
    {
        PublicAttribute => (null, true),
        InternalAttribute => (null, false),
        SharedAttribute => (BrainScope.CurrentId(), false),
        PerAccountAttribute => (caller.AccountId == id.Key ? BrainScope.Create(caller.AccountId, caller.BrainId).Id : null, false),
        _ => (id.Brain.Value, false),
    };

    // The os access policy for one call of caller into the plain neuron id. Orleans hands a streaming call to the host grain through
    // its async-enumerable extension, which no incoming filter sees, so the sink that starts a stream asks here first (06-06d).
    /// <exception cref="UnauthorizedAccessException">The policy refuses the call.</exception>
    internal static async Task Authorize(ICallFilter filter, ILogger logger, CapabilityCaller caller, DigitalBrain.NeuronId id, RuntimeInvocation call)
    {
        if (IsPlumbing(call)) { return; }
        var (scope, isPublic) = Classify(caller, id, call);
        // The grain id the neuron had as a grain, so policy stages read the same target either way.
        var target = $"{id.Type}/{id.Brain.Value}/{id.Key}";
        var decision = await filter.AuthorizeAsync(new()
        {
            Caller = caller,
            TargetNeuron = target,
            Operation = call.Method,
            EnforceTarget = true,
            TargetScope = scope,
            PublicOperation = isPublic,
        });
        if (!decision.Allowed)
        {
            logger.LogWarning("Access denied: {Principal} in brain {BrainId} calling {Operation} on plain neuron {Target}: {Explanation}",
                caller.PrincipalId, caller.BrainId, call.Method, id.Type, decision.Explanation);
            throw new UnauthorizedAccessException($"{decision.Explanation} (target {id.Type}, operation {call.Method})");
        }
    }

    // Module plumbing: a contract no edge can name, because it is not public or is [Internal] as a whole (the script catalog and the
    // neuron surface leave such contracts out, CompiledTypes).
    private static bool IsPlumbing(RuntimeInvocation call)
        => KernelInvocationSink.ContractOf(call.Contract) is { } contract && (!contract.IsVisible || contract.IsDefined(typeof(InternalAttribute), inherit: false));

    // The method's access attribute wins over its contract's; null is the owner rule.
    private static AccessAttribute? Policy(RuntimeInvocation call) => Policies.GetOrAdd((call.Contract, call.Method, call.Arguments.Length), static key =>
    {
        if (KernelInvocationSink.ContractOf(key.Contract) is not { } contract) { return null; }
        var method = new[] { contract }.Concat(contract.GetInterfaces()).SelectMany(face => face.GetMethods())
            .FirstOrDefault(candidate => candidate.Name == key.Method
                && candidate.GetParameters().Count(parameter => parameter.ParameterType != typeof(CancellationToken)) == key.Arity);
        return method?.GetCustomAttribute<AccessAttribute>() ?? method?.DeclaringType?.GetCustomAttribute<AccessAttribute>()
            ?? contract.GetCustomAttribute<AccessAttribute>();
    });
}
