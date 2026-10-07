using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Framework;

// What silo code gets for IDigitalBrain. Transitional: keys are still module-composed, so Get
// resolves the raw key and only Self needs the stamped caller.
internal sealed class CurrentBrain(IDigitalBrains brains) : IDigitalBrain
{
    public T Get<T>(NeuronId id) where T : INeuron => brains.Grain<T>(id.Name);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => brains.On<T>(neuron, cancellationToken);

    public IKernel Self => brains.Open(BrainScope.CurrentId()).Self;
}
