using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Framework;

internal sealed class NeuronCallFilter(ICallFilter filter) : IIncomingGrainCallFilter
{
    private const string ModuleCall = "digitalbrain.trusted-module-call";
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // Unstamped calls originate inside the trusted cluster (startup, reminders).
        // Public edges must stamp a caller; they cannot request this internal path.
        if (context.Grain is not Neuron neuron || !CallerContextStamper.TryGet(out var caller)
            // Credential contracts have their own parameter/ambient authority checks and
            // are excluded from every untrusted edge's contract catalog.
            || IsPlatformOnly(neuron, context.InterfaceMethod.DeclaringType!))
        { await context.Invoke(); return; }

        // A kernel-stamped fact is delivered by its publishing grain, not by the user
        // whose operation happened to produce it. Only that verified delivery boundary
        // enters internal context; ordinary calls and fabricated callbacks keep policy checks.
        if (context.InterfaceMethod.DeclaringType == typeof(INeuronObserver)
            && context.InterfaceMethod.Name == nameof(INeuronObserver.OnSignalAsync)
            && context.SourceId is { } publisher
            && !publisher.Type.ToString().StartsWith("sys.client", StringComparison.Ordinal)
            && context.Request.GetArgument(0) is Signal signal && signal.Publisher.Name == publisher.ToString())
        {
            Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
            try { await context.Invoke(); }
            finally { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, caller); }
            return;
        }

        var access = neuron.Access(context.InterfaceMethod.Name);
        // A neuron keyed by the brain id itself (workspace, app index, kernel) is in that brain.
        if (access == NeuronAccess.Unclassified && neuron.GetPrimaryKeyString() == caller.BrainId) { access = new(caller.BrainId); }
        // Module services can call through an injected client after leaving the grain
        // scheduler. Preserve that provenance without changing the end-user identity.
        var internalCall = Orleans.Runtime.RequestContext.Get(ModuleCall) is true
            || context.SourceId is { } source && !source.Type.ToString().StartsWith("sys.client", StringComparison.Ordinal);
        if (access == NeuronAccess.Unclassified && internalCall)
        { await context.Invoke(); return; }

        var decision = await filter.AuthorizeAsync(new()
        {
            Caller = caller,
            TargetNeuron = neuron.GetGrainId().ToString(),
            Operation = context.InterfaceMethod.Name,
            EnforceTarget = true,
            TargetScope = access.Scope,
            PublicOperation = access.Public,
        });
        // The target's type and operation (never its key) turn a denial from a riddle into a
        // pointer at the neuron whose classification needs a decision.
        if (!decision.Allowed)
        {
            throw new UnauthorizedAccessException(
                $"{decision.Explanation} (target {neuron.GetGrainId().Type}, operation {context.InterfaceMethod.Name})");
        }
        Orleans.Runtime.RequestContext.Set(ModuleCall, true);
        await context.Invoke();
    }

    private static bool IsPlatformOnly(Neuron neuron, Type contract)
    {
        if (PlatformOnlyAttribute.AppliesTo(contract)) { return true; }
        if (contract != typeof(INeuron)) { return false; }
        // Watch/Unwatch are declared on the shared base interface. Their target is
        // privileged only when every exposed neuron contract is privileged too.
        var contracts = neuron.GetType().GetInterfaces()
            .Where(type => type != typeof(INeuron) && typeof(INeuron).IsAssignableFrom(type)).ToArray();
        return contracts.Length > 0 && contracts.All(PlatformOnlyAttribute.AppliesTo);
    }
}
