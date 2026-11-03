using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-05b, until 06-08 moves stamping to the runtime): every outgoing grain call carries the runtime's caller stamp
// (RuntimeCallerContext, R4-20) exactly as the os stamp stands at that call: the trusted os caller as principal, account, brain
// (its scope id, the brain a NeuronId names) and app, or nothing. A plain neuron's AdmitSynapse reads it as SynapseRequest.Caller.
// Mirroring per call keeps the two in step where os code lifts its own stamp (NeuronCallFilter, CallerContextScope): a call the
// os sends unstamped never carries a runtime caller from earlier in the flow.
internal sealed class RuntimeCallerMirror : IOutgoingGrainCallFilter
{
    // RuntimeCallerContext's RequestContext keys; RuntimeCallerMirrorFacts fails if they drift.
    internal static readonly string[] Keys = ["db.cap.principal", "db.cap.account", "db.cap.brain", "db.cap.app", "db.cap.conversation"];

    public Task Invoke(IOutgoingGrainCallContext context)
    {
        Mirror();
        return context.Invoke();
    }

    internal static void Mirror()
    {
        if (CallerContextStamper.TryGet(out var caller) && CallerContextStamper.IsTrusted(caller))
        {
            // Stamp writes every key (a null app or conversation removes its key); its restore is not wanted: the os stamp it
            // mirrors is not restored either.
            _ = RuntimeCallerContext.Stamp(Of(caller));
            return;
        }
        foreach (var key in Keys) { RequestContext.Remove(key); }
    }

    internal static CapabilityCaller Of(CapabilityCaller caller)
        => caller with { BrainId = BrainScope.Create(caller.AccountId, caller.BrainId).Id };
}
