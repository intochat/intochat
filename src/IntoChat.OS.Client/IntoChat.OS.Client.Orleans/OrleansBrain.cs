using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Client.Orleans;

// One brain over the cluster client: neurons by full key, the brain neuron at {brain}/main.
internal sealed class OrleansBrain(BrainClient client, string brainId) : ISignalWatch
{
    public T Get<T>(string osFullKey) where T : class => (T)client.Get(typeof(T), osFullKey);

    public T Neuron<T>(string relative) where T : class => Get<T>($"{brainId}/{relative}");

    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => client.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => Get<IBrain>(IBrain.Key(brainId));

    public IBrainObserver Kernel => client.Kernel;
}
