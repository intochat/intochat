using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Framework;

/// <summary>
/// The caller's brain. What silo code gets when it asks DI for <see cref="IDigitalBrain"/>.
/// </summary>
/// <remarks>
/// Only <see cref="Self"/> needs the stamped caller today. Resolving is transitional: keys are
/// still module-composed, so an id is the grain key as given, and silo-internal neurons may
/// resolve with no caller stamped. Prefixing by brain lands with brain-owned keys.
/// </remarks>
internal sealed class CurrentBrain(IDigitalBrains brains) : IDigitalBrain
{
    public T Get<T>(NeuronId id) where T : INeuron => brains.Grain<T>(id.Name);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => brains.On<T>(neuron, cancellationToken);

    public IKernel Self => brains.Open(BrainScope.CurrentId()).Self;
}
