using DigitalBrain.Spi;

namespace IntoChat.OS.Runtime;

// What a runtime gives a neuron at activation. Replaces the Orleans Grain members neurons use directly
// (clean-core 2.4a): GetPrimaryKeyString, AsReference, DelayDeactivation, DeactivateOnIdle, RequestContext.
public interface INeuronContext
{
    NeuronAddress Address { get; }

    // Address.Key: the full neuron key, as GetPrimaryKeyString() returns it today.
    string Key { get; }

    IServiceProvider Services { get; }

    TimeProvider Time { get; }

    INeuronTimers Timers { get; }

    ICallerContextCarrier Caller { get; }

    ITraceCarrier Trace { get; }

    ISignalFanout Fanout { get; }

    // Reaches neurons and non-neuron endpoints (IRuntimeEndpoint) by brain-relative id or by full key.
    INeuronResolver Resolver { get; }

    // A reference to this neuron. A call through it goes through the scheduler and queues behind the current turn.
    T Self<T>() where T : class;

    // Keeps the activation alive for the duration; null cancels an earlier request. Was DelayDeactivation.
    void KeepAlive(TimeSpan? duration);

    // Deactivates once the current turn ends. Was DeactivateOnIdle.
    void Deactivate();
}

// The causal trace the runtime carries with each call. Replaces TraceContext over RequestContext.
public interface ITraceCarrier
{
    Trace? Current { get; }

    IDisposable Enter(Trace trace);
}

// Delivery of nudges to watchers (Orleans: ObserverManager and LocalSignalHub; in memory: a dictionary).
public interface ISignalFanout
{
    // Null signals watches everything; otherwise exact signal type names.
    void Watch(NeuronAddress source, INeuronObserver observer, IReadOnlySet<string>? signals);

    void Unwatch(NeuronAddress source, INeuronObserver observer);

    Task Nudge(NeuronAddress source, SignalNudge nudge, IReadOnlyList<Signal> appended);
}
