namespace DigitalBrain.Framework;

/// <summary>
/// Opens brains. The host-side seam behind <see cref="IDigitalBrain"/>: a silo or a client holds
/// one of these and opens the brain a caller is in.
/// </summary>
public interface IDigitalBrains
{
    /// <summary>The brain with this id, as a program sees it.</summary>
    IDigitalBrain Open(string brainId);

    /// <summary>
    /// A neuron by raw grain key, outside any brain. For hosts and test harnesses only; programs
    /// resolve through <see cref="IDigitalBrain.Get{T}"/>.
    /// </summary>
    T Grain<T>(string key) where T : INeuron;

    /// <summary>
    /// Streams a neuron's signals. Watching needs no brain: the reference already names its grain.
    /// </summary>
    IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal;
}
