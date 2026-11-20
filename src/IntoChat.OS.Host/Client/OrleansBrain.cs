using DigitalBrain.Brains;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Signals.Watch;
using IntoChat.OS;

namespace IntoChat.OS.Host.Client;

// One brain over the cluster client: neurons by full key, the brain neuron at {brain}/main.
internal sealed class OrleansBrain(BrainClient client, string brainId) : ISignalWatch
{
    public T Get<T>(string osFullKey) where T : class => (T)client.Get(typeof(T), osFullKey);

    public T Neuron<T>(string relative) where T : class
        => Get<T>(new DigitalBrain.NeuronId(brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId), "neuron", relative).FullKey());

    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => client.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => Get<IBrain>(IBrain.Key(brainId));

    public IBrainObserver Kernel => client.Kernel;
}
