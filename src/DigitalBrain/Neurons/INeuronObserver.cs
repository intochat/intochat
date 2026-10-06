namespace DigitalBrain;

/// <summary>
/// The receiving end of a watch: where a neuron's signals arrive. Implemented by a process-side
/// stream, or by another neuron when the connection is durable (see <see cref="Synapse"/>).
/// </summary>
public interface INeuronObserver : IGrainObserver
{
    /// <summary>Receives one signal from a watched neuron. Delivery is at least once.</summary>
    Task OnSignalAsync(Signal signal);
}
