using DigitalBrain;

namespace DigitalBrain.Client.Orleans;

// One brain over the cluster client. Transitional: keys are still module-composed, so an id is
// used as the grain key as given; prefixing with the brain lands with brain-owned keys.
internal sealed class OrleansBrain(BrainClient client, string brainId) : IDigitalBrain
{
    public T Get<T>(NeuronId id) where T : INeuron => client.Grain<T>(id.Name);

    public T Neuron<T>(NeuronId relative) where T : INeuron => Get<T>(relative.Within(brainId));

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => client.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => client.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => client.Grain<IBrain>(brainId);

    public IKernelView Kernel => new DigitalBrain.Framework.Kernel(client.Grains);
}
