using IntoChat.OS;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Client.Orleans;

// One brain over the cluster client. Transitional: keys are still module-composed, so an id is
// used as the grain key as given; prefixing with the brain lands with brain-owned keys.
internal sealed class OrleansBrain(BrainClient client, string brainId) : IIntoChatOS, IPlainClient
{
    public T Get<T>(NeuronId id) where T : INeuron => client.Grain<T>(id.Name);

    public T Neuron<T>(NeuronId relative) where T : INeuron => Get<T>(relative.Within(brainId));

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => client.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => client.On<T>(neuron, onSubscribed, cancellationToken);

    // The brain neuron is plain (06-08), keyed {brain}/main.
    public IBrain Self => (IBrain)((IPlainClient)client).GetPlain(typeof(IBrain), IBrain.Key(brainId));

    string? IPlainClient.RelativeBrain => brainId;

    public IKernelView Kernel => client.Kernel;

    object IPlainClient.GetPlain(Type contract, NeuronId id) => ((IPlainClient)client).GetPlain(contract, id);

    IAsyncEnumerable<T> IPlainClient.OnPlain<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken)
        => client.OnPlain<T>(source, onSubscribed, cancellationToken);
}
