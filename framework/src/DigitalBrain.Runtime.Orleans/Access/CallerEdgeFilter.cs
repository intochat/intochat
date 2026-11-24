using DigitalBrain.Modules;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Access;

/// <summary>
/// Enforces unforgeable edge caller invariants:
/// Ensures incoming client calls cannot claim Inside=true or Via != null.
/// Ensures incoming external client calls cannot claim internal or invalid actors (Kernel, Scheduler, Module).
/// Sets Inside based on whether the call was sent by this silo process (<see cref="SiloOrigin"/>) from a grain (SourceId not sys.client and not null).
/// </summary>
internal sealed class CallerEdgeFilter : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var raw = RequestContext.Get(OrleansCallerAmbient.Key);
        if (raw is CallerWire wire)
        {
            // The sender's grain id counts only on a call this silo process sent: a cluster client writes its own.
            var isInternalGrain = SiloOrigin.IsCurrent && context.SourceId is { } source
                && !source.Type.ToString()!.StartsWith("sys.client", StringComparison.OrdinalIgnoreCase);
            var isInternal = isInternalGrain || InProcessCallerScope.IsActive;
            var isEdgeVerified = isInternal || InProcessCallerScope.IsEdgeActive;

            if (!isEdgeVerified)
            {
                // External client: cannot forge Inside, Via, or Component
                wire.Inside = false;
                wire.Via = null;
                wire.Component = null;

                // External client: cannot claim internal or invalid actor kinds
                if (wire.Actor is not (CallerWire.ActorPerson or CallerWire.ActorAgent or CallerWire.ActorApp))
                {
                    throw new UnauthorizedAccessException(
                        $"External client cannot claim actor kind {wire.Actor}. Only Person ({CallerWire.ActorPerson}), Agent ({CallerWire.ActorAgent}), and App ({CallerWire.ActorApp}) are permitted from external callers.");
                }
            }
            else if (!isInternal)
            {
                // Verified edge caller (e.g. RelayedPerson): preserves edge-minted Via/Inside, still cannot claim Component or Kernel/Scheduler/Module.
                wire.Component = null;
                if (wire.Actor is not (CallerWire.ActorPerson or CallerWire.ActorAgent or CallerWire.ActorApp))
                {
                    throw new UnauthorizedAccessException(
                        $"External edge cannot claim actor kind {wire.Actor}. Only Person ({CallerWire.ActorPerson}), Agent ({CallerWire.ActorAgent}), and App ({CallerWire.ActorApp}) are permitted without kernel scope.");
                }
            }
            else
            {
                // Internal cluster grain or privileged in-process host call:
                // If from an internal grain or claiming internal actor authority, mark inside.
                // Otherwise (e.g. human person calling through in-process host edge), preserve direct client provenance.
                if (isInternalGrain || wire.Actor is CallerWire.ActorKernel or CallerWire.ActorScheduler or CallerWire.ActorModule)
                {
                    wire.Inside = true;
                }
            }
        }

        await context.Invoke();
    }
}
