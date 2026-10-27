using IntoChat.OS;

namespace IntoChat.OS.Client.Orleans;

// One brain over the cluster client: neurons by full key, the brain neuron at {brain}/main.
internal sealed class OrleansBrain(BrainClient client, string brainId) : IIntoChatOS
{
    public T Get<T>(NeuronId id) where T : class => (T)client.Get(typeof(T), id.Name);

    public T Neuron<T>(NeuronId relative) where T : class => Get<T>(relative.Within(brainId));

    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => client.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => Get<IBrain>(IBrain.Key(brainId));

    public IKernelView Kernel => client.Kernel;
}
