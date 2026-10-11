using DigitalBrain.Framework.Parts;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Parts;

// R144: the machines of a deployment read one reminder table, and each is its own single-silo cluster, so every reminder
// comes due on every machine. Only the holder of the neuron's part acts on a tick (R107): any other machine drops it
// before the neuron runs, so nothing is journaled or published there. A tick decides who acts, so the holder is read
// from the head rather than from a route cached before a release.
// The reminder service delivers a tick as an outgoing call: the outgoing side runs ahead of PartCallFilter, so a tick
// for a part-logged grain is dropped rather than forwarded (the holder's own reminder service fires it there) and no
// activation is made here. The incoming side, ahead of PartIncomingCallFilter, catches a tick that reached an
// activation here by another way.
internal sealed class PartReminderFilter(PartRouter router, ILogger<PartReminderFilter> logger) : IOutgoingGrainCallFilter, IIncomingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (await HeldElsewhere(context) is not { } part) { await context.Invoke(); return; }
        Drop(context, part);
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (await HeldElsewhere(context) is not { } part) { await context.Invoke(); return; }
        Drop(context, part);
        // A stand-in activated for the tick (R101) had only the tick to forward; it goes after the last call it is forwarding.
        if (!router.IsStandIn(context.TargetId)) { return; }
        router.Enter(context.TargetId);
        if (router.Leave(context.TargetId))
        { context.TargetContext.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, $"A stand-in for part {part.Part}."), CancellationToken.None); }
    }

    // The part of a reminder tick's neuron and its holder, when another machine holds it.
    private async Task<(PartId Part, string Holder)?> HeldElsewhere(IGrainCallContext context)
    {
        if (context.InterfaceMethod.DeclaringType != typeof(IRemindable) || PartRoutes.KeyedPart(context.TargetId) is not { } part) { return null; }
        router.Forget(part);
        return await router.Resolve(part) is PartRoute.Remote holder ? (part, holder.MachineId) : null;
    }

    // The tick completes without reaching the neuron: a result is what tells Orleans the chain ended here on purpose.
    private void Drop(IGrainCallContext context, (PartId Part, string Holder) held)
    {
        context.Result = null;
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Dropped reminder tick {Reminder} for {Grain}: part {Part} is held by machine {Holder}.",
                context.Request.GetArgument(0), context.TargetId, held.Part, held.Holder);
        }
    }
}
