using DigitalBrain.Units;
using DigitalBrain.Access;
using DigitalBrain.Kernel;
using DigitalBrain.Signals;
using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Signalling;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Synapses;

internal sealed class GrainNudge(
    IGrainFactory grains,
    NeuronTypeRegistry types,
    JournalPayloads? payloads = null,
    PublishFaults? faults = null,
    ILogger? logger = null) : INudge
{
    public void Nudge(NeuronId subscriber, Position upTo)
    {
        if (faults?.FailAfterAppend == true)
        {
            throw new InvalidOperationException("PublishFaults: FailAfterAppend.");
        }
        SignalTelemetry.LogNudge(logger, subscriber, upTo.Value);
        var host = HostOf(subscriber);
        using var delivering = AsBrain(subscriber.Brain);
        _ = host.Nudge(upTo.Value);
    }

    public void Offer(NeuronId subscriber, NeuronId publisher, Signal signal)
    {
        if (subscriber.Type != SessionSynapses.NeuronType || payloads is null) { return; }
        if (signal is not EmittedSignal && !payloads.Types.TryAliasOf(signal.GetType(), out _)) { return; }
        var encoded = payloads.EncodeSignal(signal);
        var (traceId, causationId) = CurrentCause.Stamp();
        if (encoded.TraceId is null && (traceId is not null || causationId is not null))
        {
            encoded = encoded with { TraceId = traceId, CausationId = causationId };
        }
        SignalTelemetry.LogOffer(logger, subscriber, publisher, encoded.Type);
        var host = HostOf(subscriber);
        using var delivering = AsBrain(subscriber.Brain);
        _ = host.Offer(publisher, encoded, DateTimeOffset.UtcNow);
    }

    // Delivery is the brain's own work (DeliveryCaller): a publish with no caller (a reaction, a reconcile sweep, a journal
    // append) or one that served a caller of another brain nudges as the scheduler of the subscriber's brain, which is the
    // publisher's. The silo refuses a call that carries no caller, and the cross-brain wall refuses the visitor's.
    private static IDisposable? AsBrain(BrainId brain)
    {
        var publishing = OrleansCallerAmbient.Instance.Current;
        var delivering = DeliveryCaller.Of(publishing, brain);
        return ReferenceEquals(delivering, publishing) ? null : OrleansCallerAmbient.Instance.Enter(delivering);
    }

    private ISynapseHost HostOf(NeuronId subscriber)
    {
        var grainType = types.TryGetRegistration(subscriber.Type, out var registration)
            ? NeuronTypeRegistry.GrainTypeOf(registration.Policy)
            : NeuronTypeRegistry.AffineGrainType;
        var grainId = GrainId.Create(grainType, subscriber.ToString());
        return grains.GetGrain<ISynapseHost>(grainId);
    }
}
