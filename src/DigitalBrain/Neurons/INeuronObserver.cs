using Orleans.Concurrency;

namespace DigitalBrain;

/// <summary>
/// The receiving end of a watch: where a neuron's nudges arrive. Implemented by a process-side
/// stream, or by another neuron when the connection is durable (see <see cref="Synapse"/>).
/// </summary>
public interface INeuronObserver : IGrainObserver
{
    /// <summary>
    /// Learns that a watched neuron has published up to <see cref="SignalNudge.Sequence"/>. Nudges are
    /// best effort and coalescable; the journal is the source of truth, read with <see cref="INeuron.Read"/>.
    /// </summary>
    [OneWay] Task Nudge(SignalNudge nudge);
}
