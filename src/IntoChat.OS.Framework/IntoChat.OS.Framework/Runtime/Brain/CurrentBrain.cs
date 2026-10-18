
namespace IntoChat.OS.Framework;

// What silo code gets for IIntoChatOS. Transitional: keys are still module-composed, so Get
// resolves the raw key and only Self needs the stamped caller.
internal sealed class CurrentBrain(IIntoChatOSBrains brains, IKernelView kernel) : IIntoChatOS
{
    public T Get<T>(NeuronId id) where T : INeuron => brains.Grain<T>(id.Name);

    public T Neuron<T>(NeuronId relative) where T : INeuron => Get<T>(relative.Within(BrainScope.CurrentId()));

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => brains.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => brains.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => brains.Open(BrainScope.CurrentId()).Self;

    public IKernelView Kernel => kernel;
}
